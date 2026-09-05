namespace Scriblucent.Services.AI;

internal interface ITokenCounter
{
    Task<int> CountTokensAsync(string text, CancellationToken ct = default);
}
