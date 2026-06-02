using System.ComponentModel;

namespace FinanceAssistant.Tools;

public class TransferFundsTool
{
    [Description("Transfer money between user accounts. This action is irreversible and the user will be asked to confirm before it runs.")]
    public Task<object> Transfer(
        [Description("Source account name, e.g. 'Checking'")] string fromAccount,
        [Description("Destination account name, e.g. 'Savings'")] string toAccount,
        [Description("Amount to transfer in account currency. Must be positive.")] decimal amount,
        CancellationToken ct = default)
    {
        // No real transfer. We log the intent and return a fake success.
        // The point of this tool is the confirmation gate, not the transfer.
        return Task.FromResult<object>(new
        {
            transferred = amount,
            from = fromAccount,
            to = toAccount,
            transactionId = Guid.NewGuid()
        });
    }
}