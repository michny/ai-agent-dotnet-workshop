using FinanceAssistant.Data;
using Microsoft.Extensions.Configuration;


await using (var db = new FinanceDbContext())
{
    await db.Database.EnsureCreatedAsync();
}

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

Console.WriteLine("Finance assistant. Type a message, or 'exit' to quit.");

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine();
    if (input is null || string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    Console.WriteLine($"(echo) {input}");
}

return 0;
