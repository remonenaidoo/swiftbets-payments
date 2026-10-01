using SwiftBets.BuildingBlocks.Messaging;
using SwiftBets.Contracts.Compliance;
using SwiftBets.Payments.Application.Ports;

namespace SwiftBets.Payments.Infrastructure.Compliance;

/// <summary>KYC status from compliance's compacted snapshot; a customer compliance has never seen is not verified.</summary>
public sealed class CompactedCustomerStatus(ICompactedState<RestrictionsChangedV1> state) : ICustomerStatus
{
    public bool IsKycVerified(Guid userId) =>
        state.TryGet(userId.ToString(), out var snapshot) && snapshot.KycStatus == KycStatus.Verified;
}
