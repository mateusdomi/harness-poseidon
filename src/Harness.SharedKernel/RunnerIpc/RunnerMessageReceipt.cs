namespace Harness.SharedKernel.RunnerIpc;

public sealed record RunnerMessageReceipt(
    string AttemptId,
    long Sequence,
    bool Applied,
    bool Replay);
