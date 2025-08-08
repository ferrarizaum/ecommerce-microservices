using OpenTelemetry.Resources;
using RabbitMQ.Client;
using StackExchange.Redis;
using System.Diagnostics.Metrics;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add Redis
var redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "redis:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConnection));

// Add RabbitMQ
var rabbitMQHost = builder.Configuration.GetConnectionString("RabbitMQ") ?? "amqp://guest:guest@rabbitmq:5672/";
builder.Services.AddSingleton<IConnection>(sp =>
{
    var factory = new ConnectionFactory { Uri = new Uri(rabbitMQHost) };
    return factory.CreateConnection();
});
builder.Services.AddSingleton<IModel>(sp => sp.GetService<IConnection>()!.CreateChannel());

// Add OpenTelemetry
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("OrderService"))
    .WithMetrics(metrics =>
    {
        metrics.AddPrometheusExporter();
        metrics.AddMeter("OrderServiceMetrics")
               .AddCounter("orders_created_total", description: "Total number of orders created");
    });

var app = builder.Build();

// Configure Prometheus endpoint
app.MapPrometheusScrapingEndpoint();

app.MapPost("/orders", async (Order order, IConnectionMultiplexer redis, IModel channel, IMeterFactory meterFactory) =>
{
    // Store order in Redis
    var db = redis.GetDatabase();
    var orderJson = JsonSerializer.Serialize(order);
    await db.StringSetAsync(order.Id, orderJson);

    // Publish message to RabbitMQ
    channel.QueueDeclare("order-queue", durable: true, exclusive: false, autoDelete: false);
    var message = JsonSerializer.Serialize(order);
    var body = System.Text.Encoding.UTF8.GetBytes(message);
    channel.BasicPublish(exchange: "", routingKey: "order-queue", basicProperties: null, body: body);

    // Record metric
    var meter = meterFactory.GetMeter("OrderServiceMetrics");
    var counter = meter.CreateCounter<long>("orders_created_total");
    counter.Add(1);

    return Results.Created($"/orders/{order.Id}", order);
});

app.Run();