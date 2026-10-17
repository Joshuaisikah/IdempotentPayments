using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// OpenAPI document generation (built-in since .NET 9, works on .NET 8+ with the package)
builder.Services.AddOpenApi();

// Controllers only needed for the classic webapi template
// (the minimal template does not need this line – it is harmless if left in)
builder.Services.AddControllers();

var app = builder.Build();

// Development-only: serve the OpenAPI document + Scalar UI
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();                       // → /openapi/v1.json
    app.MapScalarApiReference();            // → /scalar
}

app.UseHttpsRedirection();

// Map controllers when present (no-op for pure minimal APIs)
app.MapControllers();

app.MapGet("/", () => Results.Ok(new { message = "Hello from IdempotentPayments!" }));

app.Run();
