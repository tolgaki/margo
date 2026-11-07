var app = WebApplication.CreateBuilder(args).Build();
app.MapGet("/liveness", () => Results.Ok(new { status = "ok" }));
app.Run();
