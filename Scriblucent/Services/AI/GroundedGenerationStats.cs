namespace Scriblucent.Services.AI;

internal enum GroundedRejectionReason
{
    MissingRecord,
    TooShort,
    Placeholder,
    AnswerLeakage,
    SourceEcho,
    Duplicate,
    InvalidEvidence,
    UngroundedQuestion
}

internal enum GroundedQuestionDecisionKind
{
    PolicyExcluded,
    AcceptedNatural,
    Rejected
}

internal sealed record GroundedQuestionDecision(
    string FactId,
    GroundedQuestionDecisionKind Kind,
    string CandidateQuestion,
    GroundedRejectionReason? RejectionReason,
    string VerifiedAnswer = "");
