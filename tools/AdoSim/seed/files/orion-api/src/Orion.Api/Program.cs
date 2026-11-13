var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapGet("/api/orders/{id:int}", (int id) => Results.Ok(new { id, status = "Paid" }));
app.MapPost("/api/orders", (Order order) => Results.Created($"/api/orders/{order.Id}", order));

app.Run();

public sealed record Order(int Id, string Customer, decimal Total);
