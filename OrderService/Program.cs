using Microsoft.OpenApi.Models;
using OrderService.Models;
using RabbitMQ.Client;
using StackExchange.Redis;
using System.Diagnostics.Metrics;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add Redis
var redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "redis:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConnection));

// Add RabbitMQ
// CreateConnection was removed in RabbitMQ.Client v7.0.0, use CreateConnectionAsync instead,
// GetAwaiter().GetResult() is used to block on the async call,
// resulting in synchronous behavior.
// Use IChannel instead of IModel, as IModel is now obsolete.
var rabbitMQHost = builder.Configuration.GetConnectionString("RabbitMQ") ?? "amqp://guest:guest@rabbitmq:5672/";
builder.Services.AddSingleton<IConnection>(sp =>
{
    var factory = new ConnectionFactory { Uri = new Uri(rabbitMQHost) };
    return factory.CreateConnectionAsync().GetAwaiter().GetResult();
});
builder.Services.AddSingleton<IChannel>(sp => sp.GetService<IConnection>()!.CreateChannelAsync().GetAwaiter().GetResult());

// Add Swagger services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Order Service API",
        Version = "v1",
        Description = "API for managing orders with Redis and RabbitMQ integration"
    });
});

var app = builder.Build();

// Configure Swagger middleware
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Order Service API V1");
    c.RoutePrefix = string.Empty; // Serve Swagger UI at the root (e.g., /)
});

// All RabbitMQ operations are now asynchronous
app.MapPost("/orders", async (NewOrder order, IConnectionMultiplexer redis, IChannel channel, IMeterFactory meterFactory) =>
{
    // Store order in Redis
    var db = redis.GetDatabase();
    var orderJson = JsonSerializer.Serialize(order);
    await db.StringSetAsync(order.Id, orderJson);

    // Publish message to RabbitMQ
    await channel.QueueDeclareAsync("order-queue", durable: true, exclusive: false, autoDelete: false);
    var message = JsonSerializer.Serialize(order);
    var body = System.Text.Encoding.UTF8.GetBytes(message);
    await channel.BasicPublishAsync(exchange: string.Empty, routingKey: "order-queue", body: body);

    return Results.Created($"/orders/{order.Id}", order);
});

app.Run();