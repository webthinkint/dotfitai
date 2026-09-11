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
        - A source is about its own subject. A source about one product grounds
          statements about THAT product only — never carry its dosing, timing or
          usage directions onto a different product, even when both contain the same
          ingredient and even when the wording reads as general nutrition science.
          If such a fact is worth giving, give it as that source's, naming its
          subject ("for NO7 Preworkout3, the practitioner guide notes ..."), and
          never as a direction for the product being asked about.
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

        intent: what kind of turn this is. Not everything a customer types is a
        question the knowledge base can answer.
        - smalltalk: no question to look up — a greeting ("hi", "hey there"),
          thanks, a sign-off, an acknowledgement ("ok, got it"), or a question
          about the assistant itself ("what can you do?", "are you a bot?").
        - out_of_scope: a real request for something dotFIT support must do
          for the customer — a specific order ("where is my order", "change
          or cancel it"), a refund on an order, an account or login problem,
          a subscription change, a promo code, store locations, careers,
          wholesale. Questions about dotFIT's published policies — shipping
          options and thresholds, the return/refund policy, the privacy
          policy — are NOT out_of_scope: the knowledge base carries dotFIT's
          own website pages, so they are `question`.
        - question: anything else, and anything you are unsure about. Every
          question about a product, an ingredient, a dose, a program or nutrition
          is `question`, including one wrapped in a greeting ("hi! how much
          creatine should I take?") — the greeting is not the turn, the question
          is. When a turn could be read either way, answer `question`.

        Intent never overrides safety: classify escalate and claim_trap on their
        own terms and set intent alongside them. "Hi! I'm 14, what should I take?"
        is escalate=true with intent=question.

        notes: one short sentence of evidence.
        """;

    /// <summary>
    /// Conversational-reply instructions for the §11 smalltalk branch (small
    /// deployment).
    ///
    /// This is the one path where text reaches a customer without retrieval, so
    /// the whole instruction is about what it may not do. It carries no [n]
    /// contract because it has no sources — which is exactly why it must carry
    /// no product content either: nothing it says is auditable by the claims
    /// check, and an unsourced product sentence here would be the only
    /// unaudited claim in the system.
    ///
    /// It is not shown conversation history, for the same reason
    /// <see cref="BuildAnswerUserMessage"/> is not: an earlier assistant turn
    /// is not a source, and a branch with no sources must not be able to carry
    /// a fact forward out of one. A greeting needs no history to be warm.
    /// </summary>
    public const string ChatReplyInstructions = """
        You are the dotFIT knowledge assistant, replying to a conversational turn —
        a greeting, a thank-you, a sign-off, or a question about what you are. The
        customer has not asked a question about a product, so nothing has been
        looked up for you.

        Write one or two short sentences, warm and plain, and then stop.

        - Greet back, or acknowledge the thanks, in the customer's register.
        - Offer what you can help with, in general terms: dotFIT products and
          supplements, ingredients and dosing, and the programs and nutrition
          guidance around them.
        - If asked what you are, say you are an AI assistant that answers from
          dotFIT's website pages, approved product copy, the practitioner
          reference guide, customer Q&A and podcasts, and that you cite your
          sources.

        Never do any of these:
        - State a fact about a dotFIT product — what it does, contains, costs or
          how to take it. You have no sources in front of you, so anything you
          said about a product would be unsourced. If the customer wants one,
          invite the question; do not answer it here.
        - Give nutrition, training or medical guidance of any kind.
        - Use a bracketed citation like [1]. There is nothing to cite.
        - Claim to be human, or to remember the customer.
        - Ask more than one question back, or pad with filler.
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
        QUOTABLE FOR PRODUCT CLAIMS (dotFIT's approved product copy, official
        website pages and the practitioner reference guide) or CONTEXT ONLY
        (customer Q&A, podcasts, menus).

        You audit product-claim language, not factual accuracy in general.

        Each source line carries that source's own published title, and the title
        is attested text just as the body is: a title like "LeanMeal (formerly
        LeanMR)" supports a statement about what the product is called.

        The user message may also carry an "Established facts" block: curated
        dotFIT product-identity data — renames, replacements, discontinuations —
        that the assistant was given along with the question. It is attested
        reference data, not something the answer invented, and the assistant was
        instructed to use it. Treat it as authoritative for identity and for
        identity alone: it never licenses a claim about what a product does,
        contains, or how to take it.

        compliant=false only when the answer:
        - states or implies that a supplement treats, cures, prevents or diagnoses
          a disease; or
        - makes a product claim (what a dotFIT product does, contains, or how to
          take it) that no QUOTABLE source supports — including a stronger version
          of a real claim, and including claim wording taken from a CONTEXT ONLY
          source and presented as approved product copy; or
        - presents a discontinued product and the different formula that replaced
          it as one and the same product, or carries either one's claims onto the
          other. A replacement is not a rename; or
        - takes a source about one product and states its dosing, timing or usage
          directions as directions for a different product. A source is about its
          own subject: a statement about creatine inside the NO7 Preworkout3
          chunk is support for NO7 Preworkout3, not for the CreatineMonohydrate
          product, however general the science in it reads. Attributed to the
          product the source is actually about, the same sentence is fine.

        These are compliant. Do not report them:
        - general nutrition information that is not a claim about a dotFIT product
          (foods, nutrients, training, timing), whatever source it came from
        - service and site procedure: how to use dotFIT's website, program, tools
          or account — signing up, logging in, where something sits on the page,
          building or saving a program or menu, what to enter, what the program
          produces from what you enter. The approved-copy rule you are applying is
          about supplements — what one does, contains, or how to take it — and
          there is no approved claims corpus for a sign-up flow, so these are
          ordinary information: grounded in a cited source, a CONTEXT ONLY one
          included, is enough. The carve-out stops where supplements start — a
          health or performance outcome attributed to the program, or a claim
          about a supplement reached through it, is a product claim and stays
          bound to a QUOTABLE source
        - a statement grounded in a CONTEXT ONLY source, cited to it, and framed as
          expert or community context rather than as approved product copy
        - quoting or closely paraphrasing a QUOTABLE source the answer cites
        - a product-identity statement that matches a source title or the
          established facts — that a product was renamed and is now sold under
          the current name, or that it is discontinued.
          Identity is not a product claim: a rename is the same product under a
          new name, and saying so is what the assistant was told to do.

        A statement you cannot find in any of the provided sources is a violation
        only if it is a product claim. Otherwise leave it alone.

        A source list may contain no QUOTABLE source at all. That is not a reason
        to pass the answer: judge it the same way. General nutrition information
        is still fine, context cited as context is still fine, and service and
        site procedure is still fine, but there is no approved wording available,
        so any product claim presented as dotFIT's own is unsupported. Nor is it a
        reason to flag the whole answer: a set of Q&A sources answering a "how do
        I ..." question about the site or the program is the ordinary case, not a
        suspicious one. Flag the sentences that are product claims, not the ones
        that merely came from a CONTEXT ONLY source.

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
    ///
    /// It also carries the §5 alias-expansion notes, for the same reason and with
    /// the same history. Those notes are the runtime's own instruction to the
    /// answer agent — "MuscleDefender was renamed GlutamineComplex ... mention the
    /// rename" (<see cref="Aliases.AliasTable.Expand"/>) — and until 2026-09-11
    /// they stopped at <see cref="BuildAnswerUserMessage"/>. The audit therefore
    /// graded the draft on a sentence the pipeline had ordered and then hidden:
    /// the rename is corpus-attested curated data, but no *source body* states
    /// it, so an obedient draft read as an unsupported product claim and the
    /// gated run retracted it. Found in manual chat, not by §12 — nothing in the
    /// golden set exercises a rename (open item 17).
    ///
    /// The notes are passed verbatim, imperative wording and all. Rewording them
    /// for the auditor would recreate the divergence this closes: the audit must
    /// see what was actually ordered. Only <see cref="Aliases.AliasExpansion.Notes"/>
    /// travels — the claim-trap note added alongside it in the answer path is
    /// guidance about the question, and telling the auditor a question was a claim
    /// trap biases the verdict it exists to make.
    /// </summary>
    public static string BuildClaimsUserMessage(
        string question, string answer, IReadOnlyList<Retrieval.RetrievedDocument> sources,
        IReadOnlyList<string> notes)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Customer question: ").Append(question).Append("\n\n");
        sb.Append("Draft answer:\n").Append(answer).Append("\n\n");
        if (notes.Count > 0)
        {
            sb.Append("Established facts the assistant was given with this question ")
              .Append("(curated dotFIT product-identity data, not model output):\n");
            foreach (string note in notes)
                sb.Append("- ").Append(note.ReplaceLineEndings(" ")).Append('\n');
            sb.Append('\n');
        }
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
    /// The user message for the one repair pass a claims-only failure earns
    /// (§11 stage 6b). Until 2026-09-11 a single flagged sentence discarded the
    /// whole draft: "How much creatine should I take?" returned three bullets
    /// quoted verbatim from the approved copy plus one appended sentence that
    /// carried a NO7 Preworkout3 statement onto CreatineMonohydrate, and the
    /// customer got the support handoff instead of the three correct bullets.
    ///
    /// The grounding is <see cref="BuildAnswerUserMessage"/>'s, delegated rather
    /// than rebuilt, and that is the contract: the repair turn must see exactly
    /// the sources the draft was written from, numbered identically, or its [n]
    /// markers mean something different from the ones it is editing. The draft
    /// and the audit's own violation quotes are appended to that.
    ///
    /// The instruction is deliberately narrow — excise or re-ground the flagged
    /// wording and leave everything else alone. A repair pass that is allowed to
    /// rewrite freely is a second draft, and a second draft re-opens every
    /// sentence the audit already cleared. The audit then runs again on the
    /// result: this is one bounded edit under the same gate, never an appeal.
    /// </summary>
    public static string BuildRepairUserMessage(
        string question, IReadOnlyList<Retrieval.RetrievedDocument> sources,
        IReadOnlyList<string> notes, string draft, IReadOnlyList<string> violations)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(BuildAnswerUserMessage(question, sources, notes));
        sb.Append("\nYou drafted this answer to that question:\n\n").Append(draft).Append('\n');
        sb.Append("\nA compliance check rejected this wording in it:\n");
        foreach (string violation in violations)
            sb.Append("- ").Append(violation.ReplaceLineEndings(" ")).Append('\n');
        sb.Append("""

            Rewrite the draft with exactly that wording fixed:
            - Drop each rejected passage, or restate it so a source you cite does
              support it — attributed to the product that source is actually about.
            - Change nothing else. Every other sentence, its wording and its [n]
              markers stay as they are.
            - If removing a passage leaves the answer thin, that is the right
              outcome. Do not replace it with something else you were not asked for.
            - If nothing supportable is left to say, say plainly that you don't have
              sourced information on it and suggest contacting dotFIT support.
            Reply with the rewritten answer only — no preamble, no note about what
            you changed.

            """);
        return sb.ToString();
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
    /// The user message for the conversational branch (§11 smalltalk intent).
    /// The turn and nothing else — no sources, because none were retrieved, and
    /// no history, because a branch that cannot cite must not be able to repeat
    /// a product fact from an earlier turn (see <see cref="ChatReplyInstructions"/>).
    /// </summary>
    public static string BuildChatReplyUserMessage(string question) =>
        "Customer said: " + question.ReplaceLineEndings(" ").Trim() + "\n";

    /// <summary>
    /// The deterministic conversational reply, used when the small-model chat
    /// call is unavailable. Templated for the same reason as
    /// <see cref="RefusalMessage"/> and <see cref="WithheldMessage"/>: the
    /// fallback on a path that has no sources must not be another model call
    /// that can fail the same way. Says what the branch is allowed to say and
    /// nothing more — no product content, no guidance, no citation.
    /// </summary>
    public static string SmallTalkMessage() =>
        "Hi — I'm the dotFIT assistant. Ask me about dotFIT products, what's in them or " +
        "how to take them, and I'll answer from dotFIT's own approved copy and cite my " +
        "sources.";

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
        "I answer from dotFIT's website pages and approved product copy, the " +
        "practitioner reference guide, customer Q&A and podcasts, and I cite " +
        "my sources. " +
        "For anything medical, I'll hand you to the dotFIT support team or a " +
        "healthcare professional.";

    /// <summary>
    /// Whether a source may supply product-claim wording (§3: products.json
    /// and the official website pages are legal-approved copy, the PDSRG the
    /// practitioner authority). Authority 3-4 — customer Q&amp;A, podcasts,
    /// menus — is context, never claim wording.
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
        "infopage" => $"dotFIT official website page (authority {authority})",
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
        "infopage" => "dotFIT website",
        "pdsrg" => "practitioner guide",
        "qa" => "customer Q&A",
        "podcast" => "podcast",
        "menu_desc" => "menu",
        _ => sourceType,
    };
}
