var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => "FinanceAssistant.McpServer placeholder. Pillar 6 fills this in.");
app.Run();
