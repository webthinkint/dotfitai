using System.Text;
using DotFit.Agentic.Turn;
using DotFit.Agents.Answering;
using DotFit.Agents.Retrieval;

namespace DotFit.Agentic.Tools;

/// <summary>
/// The turn's source numbering (design §7), and the one place a source is
/// rendered for the model.
///
/// Three rules, all load-bearing:
///
/// 1. **Numbers are assigned at tool-result time**, first-seen order, starting
///    at 1. Three searches in a turn keep counting — they do not each restart.
/// 2. **A document keeps its number for the whole turn.** A second search that
///    returns the same id resolves to the number already assigned, so the model
///    citing "[3]" twenty seconds apart means the same source both times.
/// 3. **A number is published before it can be cited.** <see cref="Add"/>
///    queues a <see cref="TurnSourceEvent"/>, and the loop drains the queue before
///    yielding any delta. Without this, a client streaming live would see
///    "[3]" before it knew what 3 was.
///
/// Thread-safe: the model may issue parallel tool calls and the Agent
/// Framework invokes them concurrently, so numbering is under a lock rather
/// than trusting that it will not.
/// </summary>
public sealed class SourceLedger(int maxSourceChars)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SourceRef> _byId = new(StringComparer.Ordinal);
    private readonly List<SourceRef> _ordered = [];
    private readonly Queue<TurnEvent> _pending = new();
    private readonly Dictionary<string, RetrievedDocument> _documents = new(StringComparer.Ordinal);

    /// <summary>Every source assigned this turn, in citation-number order.</summary>
    public IReadOnlyList<SourceRef> Sources
    {
        get { lock (_gate) return [.. _ordered]; }
    }

    /// <summary>The document behind a citation number, for the CLI's trace view.</summary>
    public RetrievedDocument? DocumentFor(string id)
    {
        lock (_gate) return _documents.TryGetValue(id, out var doc) ? doc : null;
    }

    /// <summary>
    /// Number a document, or return the number it already has.
    /// <c>IsNew</c> distinguishes the two — the tool result tells the model when
    /// a hit is one it has already been given, which is how it learns that a
    /// rephrased search brought back nothing it did not have.
    /// </summary>
    public (SourceRef Source, bool IsNew) Add(RetrievedDocument document)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(document.Id, out SourceRef? existing))
                return (existing, false);

            var source = new SourceRef
            {
                N = _ordered.Count + 1,
                Id = document.Id,
                SourceType = document.SourceType,
                Authority = document.Authority,
                Title = document.Title,
                CitationUrl = document.CitationUrl,
                Locator = document.Locator,
                Quotable = Prompts.ClaimsQuotable(document.Authority),
            };
            _byId[document.Id] = source;
            _documents[document.Id] = document;
            _ordered.Add(source);
            _pending.Enqueue(new TurnSourceEvent(source));
            return (source, true);
        }
    }

    /// <summary>Queue a stage event from inside a tool, so it reaches the caller in call order.</summary>
    public void Stage(string stage, string? detail = null)
    {
        lock (_gate) _pending.Enqueue(new TurnStageEvent(stage, detail));
    }

    /// <summary>
    /// Take everything queued since the last drain. Called by the loop before
    /// it yields a delta and once after the run ends, so nothing is stranded.
    /// </summary>
    public IReadOnlyList<TurnEvent> Drain()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
                return [];
            var drained = new List<TurnEvent>(_pending.Count);
            while (_pending.Count > 0)
                drained.Add(_pending.Dequeue());
            return drained;
        }
    }

    /// <summary>
    /// One source as the model sees it (design §7, §8.2). The authority label
    /// and the quotable marker come from <see cref="Prompts"/> — the same §3
    /// vocabulary v1 uses, deliberately shared so there is one wording to
    /// review rather than two that drift.
    ///
    /// Long content is truncated with the cut said out loud, because a silently
    /// halved dosing table is exactly the failure <c>fetch</c> exists to fix
    /// (§7.2) and the model can only reach for it if it knows.
    /// </summary>
    public string Render(SourceRef source, RetrievedDocument document, bool isNew)
    {
        var sb = new StringBuilder();
        sb.Append("[").Append(source.N).Append("] ")
          .Append(Prompts.SourceLabel(document.SourceType, document.Authority))
          .Append(" — ").Append(Prompts.ClaimsMarker(document.Authority));
        if (!isNew)
            sb.Append(" — already given to you earlier this turn");
        sb.AppendLine();
        sb.Append("id: ").Append(document.Id).AppendLine();
        sb.Append("title: ").Append(document.Title).AppendLine();
        if (!string.IsNullOrWhiteSpace(document.Locator))
            sb.Append("locator: ").Append(document.Locator).AppendLine();
        if (document.Products.Count > 0)
            sb.Append("part numbers: ").Append(string.Join(", ", document.Products)).AppendLine();
        if (document.Date is { } date)
            sb.Append("dated: ").Append(date.ToString("yyyy-MM-dd")).AppendLine();
        if (!string.IsNullOrWhiteSpace(document.ProductStatus))
            sb.Append("product status: ").Append(document.ProductStatus).AppendLine();

        string content = document.Content ?? "";
        if (content.Length > maxSourceChars)
        {
            sb.AppendLine(content[..maxSourceChars].TrimEnd());
            sb.Append("… [truncated — call fetch(\"").Append(document.Id)
              .AppendLine("\") for the rest of this section]");
        }
        else
        {
            sb.AppendLine(content);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Citation numbers the answer actually used, ascending. Read off the
    /// delivered text rather than tracked during generation: what matters for
    /// the log is what the customer can see, not what the model was given.
    /// </summary>
    public IReadOnlyList<int> CitedIn(string answerText)
    {
        var cited = new SortedSet<int>();
        lock (_gate)
        {
            foreach (SourceRef source in _ordered)
                if (answerText.Contains($"[{source.N}]", StringComparison.Ordinal))
                    cited.Add(source.N);
        }
        return [.. cited];
    }
}
