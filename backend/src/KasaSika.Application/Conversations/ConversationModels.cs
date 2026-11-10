using KasaSika.Application.Speech;
using KasaSika.Application.Transactions;
using KasaSika.Domain.Conversations;
using KasaSika.Domain.Identity;

namespace KasaSika.Application.Conversations;

/// <param name="TurnId">Client-generated id for this utterance. Re-sending the same turn returns the same reply.</param>
/// <param name="Text">What the user typed, or what the phone's own recogniser heard.</param>
/// <param name="Audio">Raw recording for server-side recognition (Khaya). Either Text or Audio.</param>
/// <param name="InputMode">voice | text | button.</param>
/// <param name="RecognizedBy">When Text came from a recogniser on the phone, which one ("device").</param>
public sealed record TurnInput(
    Guid? ConversationId, string TurnId, string? Language, string? Text, AudioClip? Audio, string InputMode, string? RecognizedBy);

public sealed record AuthenticateInput(Guid ConversationId, Guid ChallengeId, AuthenticationMethod Method, string? Passcode, Guid? DeviceId, string? Signature);

/// <summary>What Kasa Sika understood, shown on screen as "What I heard / what I understood".</summary>
public sealed record UnderstoodView(string Intent, long? AmountMinor, string? AmountText, string? RecipientName, string? RecipientMasked);

public sealed record CandidateView(int Number, string Name, string MaskedPhone);

public sealed record AuthenticationPrompt(Guid ChallengeId, Guid TransactionId, DateTimeOffset ExpiresAt, IReadOnlyList<AuthenticationMethod> Methods, string SigningMessage);

public sealed record TurnView(string Role, string Text, string Language, string InputMode, DateTimeOffset At);

/// <summary>
/// Everything the app needs to render and voice one step of the conversation. The app never decides what
/// happens next; it shows this, speaks <see cref="Speech"/>, and (in hands-free mode) re-opens the microphone
/// when <see cref="ListenNext"/> is true.
/// </summary>
/// <param name="Speech">Null when there is nothing new to say (a silent status poll).</param>
/// <param name="Expecting">Commands that make sense now: CONFIRM, CANCEL, REPEAT, HELP, GO_BACK, CHOOSE, AUTHENTICATE.</param>
/// <param name="PollAfterMs">When set, the app should ask for status again after this delay.</param>
public sealed record ConversationReply(
    Guid ConversationId,
    ConversationStage Stage,
    string Language,
    string? Heard,
    string? HeardBy,
    UnderstoodView? Understood,
    SpeechOutput? Speech,
    string? ReplyKey,
    IReadOnlyList<string> ReplyKeys,
    IReadOnlyList<string> Expecting,
    AuthenticationPrompt? Authentication,
    TransactionView? Transaction,
    IReadOnlyList<CandidateView>? Candidates,
    bool ListenNext,
    int? PollAfterMs,
    IReadOnlyList<TurnView>? Transcript = null);
