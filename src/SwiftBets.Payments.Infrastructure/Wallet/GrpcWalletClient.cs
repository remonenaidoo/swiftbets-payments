using Grpc.Core;
using Microsoft.Extensions.Options;
using SwiftBets.BuildingBlocks.Web;
using SwiftBets.Contracts.Grpc.Wallet.V1;
using SwiftBets.Payments.Application.Ports;
using WalletGrpc = SwiftBets.Contracts.Grpc.Wallet.V1.Wallet;

namespace SwiftBets.Payments.Infrastructure.Wallet;

/// <summary>The wallet over gRPC. A refusal is a typed answer; a transport failure is unavailable and the keyed call can be repeated.</summary>
public sealed class GrpcWalletClient(WalletGrpc.WalletClient client, IOptions<WalletOptions> options, ClientCredentialsTokenProvider tokens) : IWalletClient
{
    public Task<WalletResult> OpenAccountAsync(Guid userId, string currency, CancellationToken cancellationToken) =>
        CallAsync(async deadline =>
        {
            var reply = await client.OpenAccountAsync(new OpenAccountRequest { UserId = userId.ToString(), Currency = currency }, deadline: deadline, cancellationToken: cancellationToken);
            return reply.OutcomeCase == BalanceReply.OutcomeOneofCase.Balance ? WalletResult.Ok(Guid.Parse(reply.Balance.AccountId)) : Refused(reply.Failure);
        });

    public Task<WalletResult> DepositAsync(string key, Guid accountId, long amount, string currency, string reference, CancellationToken cancellationToken) =>
        CallAsync(async deadline => Posting(await client.DepositAsync(new PostingRequest
        {
            IdempotencyKey = key,
            AccountId = accountId.ToString(),
            Amount = new Money { MinorUnits = amount, Currency = currency },
            Reference = reference,
            Reason = "deposit",
        }, deadline: deadline, cancellationToken: cancellationToken)));

    public Task<WalletResult> HoldWithdrawalAsync(string key, Guid accountId, long amount, string currency, string reference, CancellationToken cancellationToken) =>
        CallAsync(async deadline => Reservation(await client.HoldWithdrawalAsync(new ReserveRequest
        {
            IdempotencyKey = key,
            AccountId = accountId.ToString(),
            Amount = new Money { MinorUnits = amount, Currency = currency },
            Reference = reference,
        }, deadline: deadline, cancellationToken: cancellationToken)));

    public Task<WalletResult> CompleteWithdrawalAsync(string key, Guid reservationId, CancellationToken cancellationToken) =>
        CallAsync(async deadline => Reservation(await client.CompleteWithdrawalAsync(
            new ReservationCommand { IdempotencyKey = key, ReservationId = reservationId.ToString() }, deadline: deadline, cancellationToken: cancellationToken)));

    public Task<WalletResult> ReturnWithdrawalAsync(string key, Guid reservationId, CancellationToken cancellationToken) =>
        CallAsync(async deadline => Reservation(await client.ReleaseAsync(
            new ReservationCommand { IdempotencyKey = key, ReservationId = reservationId.ToString() }, deadline: deadline, cancellationToken: cancellationToken)));

    private static WalletResult Posting(PostingReply reply) =>
        reply.OutcomeCase == PostingReply.OutcomeOneofCase.Posting ? WalletResult.Ok(Guid.Parse(reply.Posting.PostingId)) : Refused(reply.Failure);

    private static WalletResult Reservation(ReservationReply reply) =>
        reply.OutcomeCase == ReservationReply.OutcomeOneofCase.Reservation ? WalletResult.Ok(Guid.Parse(reply.Reservation.ReservationId)) : Refused(reply.Failure);

    private static WalletResult Refused(WalletFailure failure) => WalletResult.Refused(Code(failure.Code), failure.Message);

    /// <summary>InsufficientFunds → insufficient_funds.</summary>
    internal static string Code(WalletFailureCode code) =>
        string.Concat(code.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? $"_{char.ToLowerInvariant(c)}" : char.ToLowerInvariant(c).ToString()));

    private async Task<WalletResult> CallAsync(Func<DateTime, Task<WalletResult>> call)
    {
        try
        {
            return await call(DateTime.UtcNow.AddSeconds(options.Value.DeadlineSeconds));
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
        {
            // A rotated identity key: drop the dead token so the retry fetches a fresh one.
            tokens.Invalidate(await tokens.GetTokenAsync(CancellationToken.None));
            throw new WalletUnavailableException("The wallet rejected the service token.", ex);
        }
        catch (RpcException ex) when (ex.StatusCode is not (StatusCode.InvalidArgument or StatusCode.PermissionDenied))
        {
            throw new WalletUnavailableException($"Wallet call failed: {ex.Status.Detail}", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or Polly.CircuitBreaker.BrokenCircuitException)
        {
            throw new WalletUnavailableException($"Wallet unreachable: {ex.Message}", ex);
        }
    }
}
