using System.Text;
using DotFit.Assistant.Turn;
using DotFit.Assistant.Retrieval;

namespace DotFit.Assistant.Tools;

/// <summary>
/// The turn's source numbering, and the one place a source is
/// rendered for the model.
///
/// Four rules, all load-bearing:
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
/// 4. **A product page is one source**. Product copy is one web page cut into
///    sections; numbered per section, a large family would be dozens of
///    near-identical "[n]"s and the model would cite the wrong ones. Sections that
///    share a <c>citation_url</c> share a number, whichever tool returned them
///    — so a search hit on one section and a later <c>get_product</c> for the
///    page are the same source. Every other type stays one number per document.
///
/// Thread-safe: the model may issue parallel tool calls and the Agent
/// Framework invokes them concurrently, so numbering is under a lock rather
/// than trusting that it will not.
/// </summary>
public sealed class SourceLedger(int maxSourceChars)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SourceRef> _byKey = new(StringComparer.Ordinal);
    private readonly List<SourceRef> _ordered = [];
    private readonly Queue<TurnEvent> _pending = new();
    private readonly Dictionary<string, RetrievedDocument> _documents = new(StringComparer.Ordinal);
    private TaskCompletionSource _queued = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
    /// What a number stands for: the page for product copy (rule 4), the
    /// document for everything else. <c>citation_url</c> is one-to-one with a
    /// product page in the index, and every section of a page carries the same
    /// title, locator, part numbers and status, so the first section seen can
    /// speak for the page.
    /// </summary>
    public static string KeyOf(RetrievedDocument document) =>
        document.SourceType == "product" && !string.IsNullOrWhiteSpace(document.CitationUrl)
            ? "page " + document.CitationUrl
            : document.Id;

    /// <summary>
    /// Number a document, or return the number it already has — its own, or
    /// its page's (rule 4).
    /// <c>IsNew</c> distinguishes the two — the tool result tells the model when
    /// a hit is one it has already been given, which is how it learns that a
    /// rephrased search brought back nothing it did not have.
    /// </summary>
    public (SourceRef Source, bool IsNew) Add(RetrievedDocument document)
    {
        lock (_gate)
        {
            // Every section stays reachable by its own id, numbered or not.
            _documents.TryAdd(document.Id, document);
            string key = KeyOf(document);
            if (_byKey.TryGetValue(key, out SourceRef? existing))
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
                Quotable = SourceLabels.ClaimsQuotable(document.Authority),
                PartNos = [.. document.Products],
            };
            _byKey[key] = source;
            _ordered.Add(source);
            Enqueue(new TurnSourceEvent(source));
            return (source, true);
        }
    }

    /// <summary>Queue a stage event from inside a tool, so it reaches the caller in call order.</summary>
    public void Stage(string stage, string? detail = null)
    {
        lock (_gate) Enqueue(new TurnStageEvent(stage, detail));
    }

    /// <summary>
    /// Completes as soon as the queue is non-empty, so the loop can wake on a
    /// tool's stage line instead of on the model's next update. A tool queues
    /// "search — <em>query</em>" and then blocks on the round trip to AI Search;
    /// the update that ends the loop's await is that same tool's result, so a
    /// loop that only drained on an update would render "looking up X" after X
    /// had been looked up. Already-completed when
    /// something is waiting, so the caller need not check first.
    /// </summary>
    public Task Queued
    {
        get { lock (_gate) return _pending.Count > 0 ? Task.CompletedTask : _queued.Task; }
    }

    /// <summary>
    /// Take everything queued since the last drain. Called by the loop each
    /// time it wakes and once after the run ends, so nothing is stranded.
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
            // Arm the next wait. Fresh rather than reset because a
            // TaskCompletionSource is single-use, and under the same lock as
            // the dequeue so no enqueue can land between emptying the queue
            // and the signal that would have announced it.
            _queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return drained;
        }
    }

    /// <summary>Enqueue and wake anyone waiting. Callers hold <c>_gate</c>.</summary>
    private void Enqueue(TurnEvent e)
    {
        _pending.Enqueue(e);
        _queued.TrySetResult();
    }

    /// <summary>
    /// One source as the model sees it. The authority label and the quotable
    /// marker come from <see cref="SourceLabels"/>, so there is one wording to
    /// review.
    ///
    /// Long content is truncated with the cut said out loud, because a silently
    /// halved dosing table is exactly the failure <c>fetch</c> exists to fix
    /// and the model can only reach for it if it knows.
    /// </summary>
    public string Render(SourceRef source, RetrievedDocument document, bool isNew)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, source, document, isNew);
        sb.Append("id: ").Append(document.Id).AppendLine();
        AppendMetadata(sb, document);
        AppendContent(sb, document);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// A product page given as one source (rule 4): the header once, then each
    /// section under its own id, in the order given. The ids stay visible
    /// because <c>fetch</c> takes a section id, and truncation stays
    /// per-section so the cut still names the fetch that fixes it.
    /// </summary>
    public string RenderPage(SourceRef source, IReadOnlyList<RetrievedDocument> sections, bool isNew)
    {
        if (sections.Count == 1)
            return Render(source, sections[0], isNew);

        var sb = new StringBuilder();
        AppendHeader(sb, source, sections[0], isNew);
        AppendMetadata(sb, sections[0]);
        sb.Append(sections.Count).AppendLine(" sections of one product page, all cited as [" + source.N + "]:");
        foreach (RetrievedDocument section in sections)
        {
            sb.AppendLine();
            sb.Append("section id: ").Append(section.Id).AppendLine();
            AppendContent(sb, section);
        }
        return sb.ToString().TrimEnd();
    }

    private static void AppendHeader(StringBuilder sb, SourceRef source, RetrievedDocument document, bool isNew)
    {
        sb.Append("[").Append(source.N).Append("] ")
          .Append(SourceLabels.SourceLabel(document.SourceType, document.Authority))
          .Append(" — ").Append(SourceLabels.ClaimsMarker(document.Authority));
        if (!isNew)
            sb.Append(" — already given to you earlier this turn");
        sb.AppendLine();
    }

    private static void AppendMetadata(StringBuilder sb, RetrievedDocument document)
    {
        sb.Append("title: ").Append(document.Title).AppendLine();
        if (!string.IsNullOrWhiteSpace(document.Locator))
            sb.Append("locator: ").Append(document.Locator).AppendLine();
        if (document.Products.Count > 0)
            sb.Append("part numbers: ").Append(string.Join(", ", document.Products)).AppendLine();
        if (document.Date is { } date)
            sb.Append("dated: ").Append(date.ToString("yyyy-MM-dd")).AppendLine();
        if (!string.IsNullOrWhiteSpace(document.ProductStatus))
            sb.Append("product status: ").Append(document.ProductStatus).AppendLine();
    }

    private void AppendContent(StringBuilder sb, RetrievedDocument document)
    {
        string content = document.Content ?? "";
        if (content.Length > maxSourceChars)
        {
            // Never split a surrogate pair: cutting between the halves of an
            // astral character hands the model a lone half, which is not text.
            int cut = maxSourceChars;
            if (cut > 0 && char.IsHighSurrogate(content[cut - 1]))
                cut--;
            sb.AppendLine(content[..cut].TrimEnd());
            sb.Append("… [truncated — call fetch(\"").Append(document.Id)
              .AppendLine("\") for the rest of this section]");
        }
        else
        {
            sb.AppendLine(content);
        }
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
