namespace KasaSika.Domain.Conversations;

/// <summary>Where a voice conversation is. The server decides; the app only renders it.</summary>
public enum ConversationStage
{
    Idle,
    AwaitingAmount,
    AwaitingRecipient,
    /// <summary>More than one contact matched ("Kofi"): the user must pick one. Never guessed.</summary>
    ChoosingRecipient,
    AwaitingConfirmation,
    AwaitingAuthentication,
    Processing,
    Completed,
}

/// <summary>
/// One user's ongoing dialogue with Kasa Sika. Holds only the slots needed to finish a request (amount,
/// recipient candidates) and a pointer to the transaction; the transaction itself carries the money state.
/// </summary>
public sealed class Conversation
{
    private Conversation() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Language { get; private set; } = "en";
    public ConversationStage Stage { get; private set; }

    public long? PendingAmountMinor { get; private set; }
    /// <summary>Spoken name that still needs resolving, e.g. "Kofi".</summary>
    public string? PendingRecipientQuery { get; private set; }
    /// <summary>Comma-separated contact ids offered to the user when the name was ambiguous.</summary>
    public string? CandidateContactIds { get; private set; }
    public Guid? PendingContactId { get; private set; }
    public Guid? TransactionId { get; private set; }

    /// <summary>The last thing Kasa Sika said (template key + arguments as JSON), so "repeat" is exact.</summary>
    public string? LastReplyKey { get; private set; }
    public string? LastReplyArgs { get; private set; }

    /// <summary>Snapshot of the previous stage and slots, for "go back". One level is enough to undo a mistake.</summary>
    public string? PreviousSnapshot { get; private set; }

    public int TurnCount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public int Version { get; private set; }

    public static Conversation Start(Guid userId, string language, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Language = language,
        Stage = ConversationStage.Idle,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public IReadOnlyList<Guid> Candidates =>
        string.IsNullOrEmpty(CandidateContactIds) ? [] : CandidateContactIds.Split(',').Select(Guid.Parse).ToList();

    public void SetLanguage(string language) => Language = language;

    public void Touch(DateTimeOffset now)
    {
        TurnCount++;
        UpdatedAt = now;
        Version++;
    }

    public void SetAmount(long? amountMinor) => PendingAmountMinor = amountMinor;

    public void SetRecipientQuery(string? query) => PendingRecipientQuery = query;

    public void SetContact(Guid? contactId)
    {
        PendingContactId = contactId;
        CandidateContactIds = null;
    }

    public void OfferCandidates(IEnumerable<Guid> contactIds) => CandidateContactIds = string.Join(",", contactIds);

    public void AttachTransaction(Guid? transactionId) => TransactionId = transactionId;

    public void MoveTo(ConversationStage stage) => Stage = stage;

    public void Remember(string replyKey, string argsJson)
    {
        LastReplyKey = replyKey;
        LastReplyArgs = argsJson;
    }

    public void SaveSnapshot(string snapshotJson) => PreviousSnapshot = snapshotJson;

    public void ClearSnapshot() => PreviousSnapshot = null;

    /// <summary>Forget every slot and return to the beginning. The transaction (if any) is handled by the caller.</summary>
    public void Reset()
    {
        Stage = ConversationStage.Idle;
        PendingAmountMinor = null;
        PendingRecipientQuery = null;
        CandidateContactIds = null;
        PendingContactId = null;
        TransactionId = null;
        PreviousSnapshot = null;
    }
}

/// <summary>
/// One exchange in a conversation, kept so the visual transcript survives a reload and so an assessor can
/// see in PostgreSQL what was heard and what was said. Never contains passcodes: authentication does not
/// go through the conversation.
/// </summary>
public sealed class ConversationTurn
{
    private ConversationTurn() { }

    public Guid Id { get; private set; }
    public Guid ConversationId { get; private set; }
    public int Sequence { get; private set; }
    /// <summary>"user" or "assistant".</summary>
    public string Role { get; private set; } = "";
    public string Text { get; private set; } = "";
    public string Language { get; private set; } = "";
    /// <summary>voice, text, button, system.</summary>
    public string InputMode { get; private set; } = "";
    /// <summary>Which speech recogniser produced the text, if any ("khaya:twi", "device").</summary>
    public string? RecognizedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static ConversationTurn Create(Guid conversationId, int sequence, string role, string text, string language, string inputMode, string? recognizedBy, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = conversationId,
        Sequence = sequence,
        Role = role,
        Text = text.Length > 500 ? text[..500] : text,
        Language = language,
        InputMode = inputMode,
        RecognizedBy = recognizedBy,
        CreatedAt = now,
    };
}
