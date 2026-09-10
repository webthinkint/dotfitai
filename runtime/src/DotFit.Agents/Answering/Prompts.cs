namespace DotFit.Agents.Answering;

/// <summary>
/// Every prompt the runtime owns (plan §11). Wording decisions live here, in
/// code, where they are diffed and tested — not in config files. Line endings
/// are always "\n" so the prompt bytes are identical on every platform.
/// </summary>
public static class Prompts
{
    /// <summary>Grounded-answer agent instructions (chat deployment).</summary>
    public const string AnswerInstructions = """
        You are the dotFIT knowledge assistant — an AI assistant that answers customer
        questions about dotFIT nutrition products, supplements, and programs using ONLY
        the numbered sources provided in the user message.

        Identity and posture
        - You are an AI assistant; say so plainly if asked, and never claim to be human.
        - You provide nutrition guidance, not medical advice.
        - If the sources do not answer the question, say so honestly. Never invent
          product facts, doses, or policies.

        Citations — hard requirement
        - Cite every factual claim with a bracketed source number, like [1] or [2].
        - Every source is tagged either QUOTABLE FOR PRODUCT CLAIMS or CONTEXT ONLY.
          A product claim (what a product does, contains, or how to take it) should be
          quoted from a QUOTABLE FOR PRODUCT CLAIMS source and cite that source's number.
        - CONTEXT ONLY sources may inform framing and general nutrition context, but
          never supply claim wording. If a claim appears ONLY in a CONTEXT ONLY
          source, do not state it as a product claim: attribute it plainly as expert or
          community context. Never attach a QUOTABLE source's
          number to wording that source does not contain.
        - Never cite a source that was not provided, and never cite from memory.

        Hard escalation — refuse and hand off
        If the question involves any of the following, do NOT give guidance: briefly
        say you can't help with that one, and tell the user to contact the dotFIT
        support team or a healthcare professional.
        - pregnancy or breastfeeding
        - a managed medical condition (diabetes, heart disease, kidney disease, ...)
        - eating-disorder signals
        - anyone under 16
        - interactions with prescription medication
        - extreme calorie targets
        - self-harm

        Style
        - Be concise and specific; use the sources' own wording for claims.
        - If a source notes a product rename (for example "LeanMR, now LeanMeal"),
          use the current name.
        - If the question presumes a claim the approved copy does not make (that a
          supplement treats, cures, or prevents a disease), do not endorse it —
          correct it politely and answer only from approved copy.
        - Short plain paragraphs; a list only when it genuinely helps. No headings
          for a short answer.
        """;

    /// <summary>Guardrail pre-check instructions (small deployment).</summary>
    public const string GuardrailInstructions = """
        You are the safety pre-check for the dotFIT knowledge assistant (nutrition
        supplements, plans and programs). Classify the customer question, read in
        the conversation it was asked in.

        Escalate (escalate=true, with reasons) when the question asks for guidance
        that must come from a human, not an AI assistant:
        - pregnancy_or_breastfeeding: any guidance while pregnant or breastfeeding
        - managed_condition: a diagnosed or managed condition (diabetes, heart,
          kidney, thyroid, hypertension, ...)
        - eating_disorder: eating-disorder signals (restriction, purging, extreme
          fear of weight gain, ...)
        - under_18: guidance for a child or teenager
        - medication_interaction: interactions with prescription medication
        - extreme_calorie_target: very low or extreme calorie targets
        - self_harm: any self-harm signal
        - other: anything else that clearly needs a clinician
        Be conservative: when in doubt, escalate.

        claim_trap=true when the question presumes a non-compliant product claim —
        for example asks whether a supplement cures, treats, or prevents a disease
        ("does X cure diabetes", "will X lower my blood pressure"), or states one
        as fact.

        The user message may open with a "Recent conversation" block: earlier turns
        of this same conversation, oldest first. A customer states something about
        themselves once and then keeps talking, so judge the current question with
        what they have already told you.
        - A hard-escalation trigger stated in ANY earlier turn still applies. "I'm
          14" three turns ago and "how much creatine should I take?" now is one
          question from a minor: escalate, reason under_18.
        - The same for a trigger the assistant surfaced and the customer confirmed
          ("are you pregnant?" / "yes, 12 weeks").
        - claim_trap likewise carries: "will Omega-3 lower my blood pressure?" then
          "which one should I get?" is still the same presumed claim.
        - Only escalate when the trigger bears on what is being asked NOW. The
          history is context for this question, not a second question to answer,
          and a trigger does not put every later turn behind a refusal — an order,
          shipping or "what is in this product" question is not guidance for the
          condition.
        - A trigger that belongs to someone the customer is not asking guidance
          for is not their trigger. Guidance for a minor or for a pregnant person
          escalates whoever is typing; a customer mentioning a relative's condition
          while asking about themselves does not.
        - The assistant's own words are not evidence about the customer. Its
          standing "this is not medical advice, see a professional" line names no
          trigger.
        - When the current question stands alone, judge it alone.

        history_trigger=true when your verdict — escalate or claim_trap — rests on
        an earlier turn rather than on the current question. False when the current
        question carries the trigger by itself, and false whenever both are false.

        notes: one short sentence of evidence.
        """;

    /// <summary>Query-rewrite instructions (small deployment).</summary>
    public const string RewriteInstructions = """
        You normalize customer questions about dotFIT nutrition products.
        - canonical_question: a self-contained, standalone version of the question.
          Fix obvious typos lightly and keep the customer's meaning; never add facts.
        - product_mentions: the dotFIT products the question is about, as family
          names from the known-families list in the user message when one matches,
          otherwise the customer's own words. Empty when no product is mentioned.
          Never invent products.
        - topics: zero to three short topic tags (for example "creatine",
          "meal timing", "multivitamin").
        - confidence: 0 to 1, how sure you are about the product mentions.

        The user message may open with a "Recent conversation" block: earlier turns
        of this same conversation, oldest first. It is there for one job — to
        resolve what the current question leaves implicit.
        - Resolve pronouns and ellipsis against it ("is it safe with coffee?",
          "what about the chocolate one?", "how much should I take?").
        - A product named earlier and referred back to is a product_mention now.
        - canonical_question must read as a standalone question that needs none of
          the history to understand.
        - When the current question already stands alone, ignore the history — a
          customer changing the subject is not asking a follow-up.
        - Never answer the question, never carry a fact out of an earlier assistant
          turn into canonical_question, and never widen what is being asked. You
          are rewriting one question, not summarizing the conversation.
        """;

    /// <summary>Claims-language post-check instructions (small deployment).</summary>
    public const string ClaimsInstructions = """
        You audit a draft answer from the dotFIT knowledge assistant for compliant
        claim language. You are given every source the answer was allowed to use,
        numbered exactly as the answer's [n] citations are, each tagged either
        QUOTABLE FOR PRODUCT CLAIMS (dotFIT's approved product copy and the
        practitioner reference guide) or CONTEXT ONLY (customer Q&A, podcasts,
        menus).

        You audit product-claim language, not factual accuracy in general.

        compliant=false only when the answer:
        - states or implies that a supplement treats, cures, prevents or diagnoses
          a disease; or
        - makes a product claim (what a dotFIT product does, contains, or how to
          take it) that no QUOTABLE source supports — including a stronger version
          of a real claim, and including claim wording taken from a CONTEXT ONLY
          source and presented as approved product copy.

        These are compliant. Do not report them:
        - general nutrition information that is not a claim about a dotFIT product
          (foods, nutrients, training, timing), whatever source it came from
        - a statement grounded in a CONTEXT ONLY source, cited to it, and framed as
          expert or community context rather than as approved product copy
        - quoting or closely paraphrasing a QUOTABLE source the answer cites

        A statement you cannot find in any of the provided sources is a violation
        only if it is a product claim. Otherwise leave it alone.

        A source list may contain no QUOTABLE source at all. That is not a reason
        to pass the answer: judge it the same way. General nutrition information
        is still fine, and context cited as context is still fine, but there is no
        approved wording available, so any product claim presented as dotFIT's own
        is unsupported.

        violations: short quotes of the offending phrasing from the answer.
        evidence: one entry per violation, in the same order, each starting with the
        source number you checked it against — "[4] the copy says ..." — or "none"
        when no provided source supports it. Never cite a number that is not in the
        list you were given.
        """;

    /// <summary>
    /// The user message for the guardrail pre-check (§11 stage 1).
    ///
    /// Carries the same history block as <see cref="BuildRewriteUserMessage"/>
    /// and for the opposite reason: the rewrite needs it to know what is being
    /// asked, the guardrail needs it to know who is asking (open item 19). A
    /// hard-escalation trigger is a fact about the customer — "I'm 14",
    /// "I'm 12 weeks pregnant", "I take warfarin" — and a customer states it
    /// once. Judged turn by turn, the pre-check reads "how much creatine?" as
    /// an ordinary product question and the pipeline answers a minor.
    ///
    /// The block is deliberately labelled as context for the current question,
    /// not as a transcript to classify: the failure mode on this side is a
    /// conversation that escalates once and then refuses everything after it,
    /// including "where is my order".
    /// </summary>
    public static string BuildGuardrailUserMessage(
        string question, IReadOnlyList<ConversationTurn> history)
    {
        var sb = new System.Text.StringBuilder();
        if (history.Count > 0)
        {
            sb.Append("Recent conversation (oldest first, context for the question below):\n");
            foreach (ConversationTurn turn in history)
            {
                sb.Append(turn.Role == ConversationRole.User ? "customer: " : "assistant: ")
                  .Append(turn.Text.ReplaceLineEndings(" ")).Append('\n');
            }
            sb.Append('\n');
        }
        sb.Append("Customer question: ").Append(question).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// The user message for the query rewrite (§11 stage 2). The conversation
    /// history block is what makes a follow-up answerable at all — "what about
    /// the chocolate one?" has nothing to resolve "the chocolate one" against
    /// without it, and retrieves on the words "chocolate one" (open item 18).
    ///
    /// This and <see cref="BuildGuardrailUserMessage"/> are the only prompts in
    /// the runtime that are shown history.
    /// <see cref="BuildAnswerUserMessage"/> is deliberately not: an answer
    /// grounded in anything but the retrieved sources cannot honour the [n]
    /// citation contract, and an earlier assistant turn is not a source. The
    /// history's whole effect on the answer is the canonical question it
    /// produces and the product mentions it resolves.
    /// </summary>
    public static string BuildRewriteUserMessage(
        string question,
        IReadOnlyList<string> knownFamilies,
        IReadOnlyList<ConversationTurn> history)
    {
        var sb = new System.Text.StringBuilder();
        if (history.Count > 0)
        {
            sb.Append("Recent conversation (oldest first, for resolving references only):\n");
            foreach (ConversationTurn turn in history)
            {
                sb.Append(turn.Role == ConversationRole.User ? "customer: " : "assistant: ")
                  .Append(turn.Text.ReplaceLineEndings(" ")).Append('\n');
            }
            sb.Append('\n');
        }
        sb.Append("Customer question: ").Append(question).Append("\n\n");
        sb.Append("Known dotFIT product families: ").Append(string.Join("; ", knownFamilies)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Grounded context for the answer agent. Numbering is the citation contract.</summary>
    public static string BuildAnswerUserMessage(
        string question, IReadOnlyList<Retrieval.RetrievedDocument> sources, IReadOnlyList<string> notes)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Customer question: ").Append(question).Append("\n\n");

        if (sources.Count == 0)
        {
            sb.Append("No sources were found for this question.\n");
            sb.Append("Answer honestly: say you don't have sourced information on it and " +
                      "suggest contacting dotFIT support. Do not guess.\n");
        }
        else
        {
            sb.Append("Sources (cite every factual claim as [n]):\n");
            for (int i = 0; i < sources.Count; i++)
            {
                var doc = sources[i];
                sb.Append('[').Append(i + 1).Append("] ").Append(doc.Title)
                  .Append(" — ").Append(SourceLabel(doc.SourceType, doc.Authority))
                  .Append(" — ").Append(ClaimsMarker(doc.Authority)).Append('\n');
                if (!string.IsNullOrEmpty(doc.CitationUrl))
                    sb.Append("    url: ").Append(doc.CitationUrl).Append('\n');
                if (!string.IsNullOrEmpty(doc.Locator))
                    sb.Append("    locator: ").Append(doc.Locator).Append('\n');
                if (doc.Date is { } date)
                    sb.Append("    dated: ").Append(date.ToString("yyyy-MM-dd")).Append('\n');
                sb.Append("    ").Append(doc.Content.ReplaceLineEndings(" ")).Append("\n\n");
            }
        }

        if (notes.Count > 0)
        {
            sb.Append("Notes:\n");
            foreach (string note in notes)
                sb.Append("- ").Append(note).Append('\n');
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// The user message for the claims-language post-check. Carries *every*
    /// retrieved source, numbered exactly as <see cref="BuildAnswerUserMessage"/>
    /// numbers them, so the checker resolves the draft's [n] markers against the
    /// same list the answer agent wrote from.
    ///
    /// Passing approved copy alone — what this did until 2026-09-10 — left the
    /// checker blind to the sources most of those markers point at, so anything
    /// grounded in the Q&amp;A or podcast corpus was indistinguishable from an
    /// invention. The 2026-09-09 dev sweep withheld 51/125 answers, 50 of them on
    /// claims_language; 15 of those cited nothing the checker could see, one being
    /// a one-line answer about the carbohydrates in an apple, which is not a
    /// product claim at all (open items 17 and 12). The §3 claims line is carried
    /// by <see cref="ClaimsMarker"/> on each source, not by omitting sources.
    /// </summary>
    public static string BuildClaimsUserMessage(
        string question, string answer, IReadOnlyList<Retrieval.RetrievedDocument> sources)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Customer question: ").Append(question).Append("\n\n");
        sb.Append("Draft answer:\n").Append(answer).Append("\n\n");
        sb.Append("Sources the answer was given (numbered as its [n] citations):\n");
        for (int i = 0; i < sources.Count; i++)
        {
            var doc = sources[i];
            sb.Append('[').Append(i + 1).Append("] ").Append(doc.Title)
              .Append(" — ").Append(SourceLabel(doc.SourceType, doc.Authority))
              .Append(" — ").Append(ClaimsMarker(doc.Authority)).Append('\n');
            sb.Append("    ").Append(doc.Content.ReplaceLineEndings(" ")).Append("\n\n");
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// Deterministic refusal for guardrail-escalated questions — templated on
    /// purpose: no LLM call on the escalation path, and the post-check can
    /// verify the handoff wording exactly.
    /// </summary>
    public static string RefusalMessage(
        IReadOnlyList<string> displayReasons, string? supportContact = DefaultSupportContact)
    {
        string because = displayReasons.Count == 0
            ? ""
            : $" — especially as it involves {string.Join(" and ", displayReasons)}";
        return "Thanks for the question — this is one I need to hand off rather than answer here" +
               because + ". Please contact the dotFIT support team or a healthcare professional, " +
               "and they'll take good care of you.\n\n" +
               SupportLine(supportContact) +
               "(I'm an AI assistant — nutrition guidance, not medical advice.)";
    }

    /// <summary>
    /// The support route the two handoffs end on, or <c>""</c> when a
    /// deployment configures none (open item 22: a real route in the handoff
    /// templates "instead of prose" — these two templates are the most-seen
    /// copy on the failure paths, and a dead end is a bad outcome for a
    /// customer already being turned away).
    /// </summary>
    private static string SupportLine(string? supportContact) =>
        string.IsNullOrWhiteSpace(supportContact)
            ? ""
            : $"You can reach dotFIT support at {supportContact.Trim()}.\n\n";

    /// <summary>
    /// The route the handoffs use unless a deployment overrides it
    /// (<c>DOTFIT_SUPPORT_CONTACT</c>, see
    /// <see cref="Config.RuntimeOptions.SupportContact"/>).
    ///
    /// Corpus-attested, not invented: the PDSRG's own "About dotFIT Worldwide"
    /// section publishes this mailbox and toll-free number as the route for
    /// consumers and professionals, which makes it authority-2 approved copy
    /// rather than a guess — the same standard §5 holds an alias to. Owner
    /// ruling 2026-09-10 (open item 22): ship it as the default and keep it
    /// overridable, since a deployment may route the preview audience
    /// somewhere else.
    /// </summary>
    public const string DefaultSupportContact = "support@dotfit.com or (877) 436-8348";

    /// <summary>
    /// Delivered instead of the answer when a gated run fails the post-check
    /// (<see cref="DotFit.Agents.AnswerStreamMode.Gated"/>). Templated for the same reason as
    /// <see cref="RefusalMessage"/> — the fallback for a failed check must not
    /// itself be a model call that can fail the same check. Deliberately does
    /// not name the failure: the customer gets a handoff, the trace gets the
    /// detail.
    /// </summary>
    public static string WithheldMessage(string? supportContact = DefaultSupportContact) =>
        "I wasn't able to give you a sourced answer I'm confident in on that one, so I'd " +
        "rather not guess. Please contact the dotFIT support team and they'll take good " +
        "care of you.\n\n" +
        SupportLine(supportContact) +
        "(I'm an AI assistant — nutrition guidance, not medical advice.)";

    /// <summary>
    /// AI-identity disclosure for the start of a conversation (§11 standing
    /// behaviors). The refusal and withheld templates carry their own copy
    /// because they may be the only thing a customer ever sees; this is the
    /// one the SSE service emits before the first answer, so a customer who
    /// only ever gets good answers is still told what they are talking to.
    /// Kept here rather than in the service so there is one wording to review,
    /// not two.
    /// </summary>
    public static string ConversationDisclosure() =>
        "I'm an AI assistant — nutrition guidance, not medical advice. " +
        "I answer from dotFIT's approved product copy, the practitioner " +
        "reference guide, customer Q&A and podcasts, and I cite my sources. " +
        "For anything medical, I'll hand you to the dotFIT support team or a " +
        "healthcare professional.";

    /// <summary>
    /// Whether a source may supply product-claim wording (§3: products.json is
    /// the legal-approved claims corpus, the PDSRG the practitioner authority).
    /// Authority 3-4 — customer Q&amp;A, podcasts, menus — is context, never
    /// claim wording.
    /// </summary>
    public static bool ClaimsQuotable(int authority) => authority <= 2;

    /// <summary>
    /// The per-source tag <see cref="AnswerInstructions"/> keys off. Prose alone
    /// was not enough: the 2026-09-08 smoke lifted "muscle, cognitive, or
    /// anti-aging benefits" from an authority-3 Q&amp;A and cited it beside an
    /// authority-2 chunk that never said it — which the deterministic
    /// product_claim_citation check cannot see, because the citation *is* to an
    /// approved source. Tagging every source makes the rule structural.
    /// </summary>
    public static string ClaimsMarker(int authority) => ClaimsQuotable(authority)
        ? "QUOTABLE FOR PRODUCT CLAIMS"
        : "CONTEXT ONLY";

    /// <summary>
    /// Model-facing source label per §3/§9 source type — what the answer agent
    /// sees on each numbered source, and therefore what it echoes when it
    /// attributes an answer in prose. That echo is why these read as
    /// provenance rather than as the name of the corpus they came from: the
    /// literal corpus name surfaced in answers as "dotFIT's customer Q&amp;As
    /// typically recommend ...", which tells a customer how the corpus was
    /// assembled instead of where the guidance comes from. The customer-facing
    /// citation line is <see cref="SourceKind"/>, which is separate and
    /// deliberately still literal. Quotability is carried by
    /// <see cref="ClaimsMarker"/>, never by this label, so rewording here
    /// cannot move a source across the §3 claims line.
    /// </summary>
    public static string SourceLabel(string sourceType, int authority) => sourceType switch
    {
        "product" => $"dotFIT approved product copy (authority {authority})",
        "pdsrg" => $"Practitioner Dietary Supplement Reference Guide (authority {authority})",
        "qa" => $"dotFIT nutrition knowledge base (authority {authority})",
        "podcast" => $"dotFIT expert discussion transcript (authority {authority})",
        "menu_desc" => $"dotFIT menu description (authority {authority})",
        _ => $"{sourceType} (authority {authority})",
    };

    /// <summary>Short kind for citation lines.</summary>
    public static string SourceKind(string sourceType) => sourceType switch
    {
        "product" => "product copy",
        "pdsrg" => "practitioner guide",
        "qa" => "customer Q&A",
        "podcast" => "podcast",
        "menu_desc" => "menu",
        _ => sourceType,
    };
}
