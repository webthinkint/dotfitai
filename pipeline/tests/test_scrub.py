"""Stage 0 scrub tests (pure functions — fast, no filesystem for the core)."""

from __future__ import annotations

from qa_pipeline.extract import extract_docx
from qa_pipeline.scrub import (
    ADDRESS_RE, CARD_RE, EMAIL_RE, GREETING_NAME_TOKENS, PHONE_RE,
    PROFILE_URL_RE, SSN_RE, scrub_extracted,
)

import pytest


class TestPatterns:
    def test_emails(self):
        assert EMAIL_RE.search("contact: JANE.X@EXAMPLE.COM")
        assert EMAIL_RE.search("first.last+tag@sub.example.org")

    def test_phone_formats(self):
        for s in ("877-436-8348", "805.435.1414", "(805) 435-1414",
                  "+1 805 435 1414", "1-800-555-0192"):
            assert PHONE_RE.search(s), s

    def test_phone_does_not_match_dosages_or_dates(self):
        for s in ("1000-1200 mgs", "2008-2023", "take 3-5 times daily",
                  "5 g creatine", "12/26/2023"):
            assert not PHONE_RE.search(s), s

    def test_ssn(self):
        assert SSN_RE.search("SSN: 123-45-6789")
        assert not SSN_RE.search("800-555-0192")  # phone shape is 3-3-4

    def test_card(self):
        assert CARD_RE.search("card 4111 1111 1111 1111")
        assert CARD_RE.search("4111-1111-1111-1111")
        assert not CARD_RE.search("take 1000-1200 mg")  # too few digit groups

    def test_profile_urls_kept_out_generic(self):
        assert PROFILE_URL_RE.search("see https://www.facebook.com/jane.q.customer")
        assert PROFILE_URL_RE.search("instagram.com/janefit")
        assert not PROFILE_URL_RE.search("https://www.dotfit.com/product")

    def test_profile_urls_short_and_new_domains(self):
        # fb.me / x.com / threads.net carry no .com suffix — the old
        # host+`.com` construction never matched them (review fix)
        assert PROFILE_URL_RE.search("see https://x.com/someuser")
        assert PROFILE_URL_RE.search("see https://www.threads.net/t/abc")
        assert PROFILE_URL_RE.search("see https://fb.me/xyz")
        text, rep = scrub_extracted(["my page https://x.com/someuser here"])
        assert "[PROFILE-URL]" in text
        assert "x.com/someuser" not in text

    def test_profile_url_does_not_match_inside_longer_domains(self):
        # x.com is a suffix of nxgenrx.com — without a left boundary the
        # short domain eats the tail of the longer one (regen catch)
        url = "https://nxgenrx.com/#/dotfit"
        assert not PROFILE_URL_RE.search(url)
        text, _ = scrub_extracted([url])
        assert text.strip() == url


class TestScrubExtracted:
    def test_email_thread_scrub(self, email_thread_docx):
        extraction = extract_docx(email_thread_docx)
        assert extraction.ok
        text, rep = scrub_extracted(extraction.lines)

        # structural
        assert "Copyright 2008" not in text
        assert "Disclaimer:" not in text
        assert "Alice Expert" not in text          # signature dropped
        assert "Office. 555.123.4567" not in text
        assert "Hi [CUSTOMER]," in text            # greeting de-named
        # contact block redacted but labels survive for stage 1
        assert "From: [CUSTOMER]" in text
        assert "Email: [EMAIL]" in text
        assert "Phone: [PHONE]" in text
        # question content kept, embedded PII pattern-scrubbed
        assert "How many fish oil capsules" in text
        assert "jane.customer@example.com" not in text
        assert "877-555-3418" not in text
        # section split markers survive
        assert "Sent: Sunday, February 26, 2023" in text
        # report
        assert rep.ok and rep.header_detected and rep.signature_cut
        assert rep.greeting_name_redacted
        assert rep.redactions.get("email_field", 0) >= 1

    def test_expert_note_scrub(self, expert_note_docx):
        extraction = extract_docx(expert_note_docx)
        text, rep = scrub_extracted(extraction.lines)
        assert rep.ok
        assert not rep.header_detected
        assert not rep.signature_cut
        assert "Practitioner Product Status" in text
        assert "https://example-lab.org/report-2023" in text  # generic URL kept

    def test_determinism(self, email_thread_docx):
        extraction = extract_docx(email_thread_docx)
        t1, r1 = scrub_extracted(extraction.lines)
        t2, r2 = scrub_extracted(extraction.lines)
        assert t1 == t2 and r1.to_dict() == r2.to_dict()

    def test_unparseable(self, tmp_path):
        bad = tmp_path / "bad.docx"
        bad.write_bytes(b"this is not a zip file")
        extraction = extract_docx(bad)
        assert not extraction.ok
        assert extraction.error and "BadZipFile" in extraction.error


class TestResidualHonorificAllowlist:
    """Accepted honorific names (owner dispositions) do not flag; others do."""

    def test_unknown_name_flags(self):
        text, rep = scrub_extracted([
            "as Dr. Smith noted, take with food."])
        assert rep.flags.count("honorific_plus_name") == 1
        assert rep.residual_pii_flag is True

    def test_honorific_without_period_flags(self):
        # "Dr Smith" (no period) is the same person-reference and must
        # flag like the dotted form (review fix)
        _, rep = scrub_extracted(["as Dr Smith noted, take with food."])
        assert "honorific_plus_name" in rep.flags
        assert rep.residual_pii_flag is True

    def test_accepted_name_does_not_flag(self):
        text, rep = scrub_extracted([
            "as Dr. Katz noted, take with food."])
        assert "honorific_plus_name" not in rep.flags
        assert rep.residual_pii_flag is False

    def test_street_address_is_redacted_not_flagged(self):
        """Addresses are redacted (plan §4 step 3), so the trailing "Dr."
        can no longer masquerade as an honorific residue."""
        text, rep = scrub_extracted([
            "8600 Sample Park Dr. Springfield, IL 62704"])
        assert text.strip() == "[ADDRESS]"
        assert "honorific_plus_name" not in rep.flags
        assert rep.residual_pii_flag is False

    def test_accept_list_matches_surname_position_only(self):
        """A stranger sharing an accepted first name still flags."""
        _, ok = scrub_extracted(["as Dr. Jules Hirsch reported,"])
        assert "honorific_plus_name" not in ok.flags
        _, bad = scrub_extracted(["as Dr. Hirsch Nakamura reported,"])
        assert "honorific_plus_name" in bad.flags

    def test_honorific_gap_never_crosses_a_blank_line(self):
        # "lean Mr" (the LeanMR product) + a Thanks closer is not a person
        # reference — the old \\s+ gap fused them into a false flag
        _, rep = scrub_extracted(
            ["It was for the chocolate lean Mr", "", "Thanks,"])
        assert "honorific_plus_name" not in rep.flags
        assert rep.residual_pii_flag is False

    def test_wrapped_honorific_still_flags(self):
        _, rep = scrub_extracted(["as Dr.", "Smith noted, take with food."])
        assert "honorific_plus_name" in rep.flags

    def test_middle_initial_does_not_hide_the_surname(self):
        # capture runs to the surname, so the allowlist is tested on it:
        # Katz is accepted, Rivera is not
        _, ok = scrub_extracted(["as Dr Alex M. Katz noted, take with food."])
        assert "honorific_plus_name" not in ok.flags
        _, bad = scrub_extracted(
            ["as Dr Alex M. Rivera noted, take with food."])
        assert "honorific_plus_name" in bad.flags


class TestAddressRedaction:
    def test_common_street_forms(self):
        for s in ("19835 Sample St", "321 Example Ave #94",
                  "32107 Sample Canyon Rd.", "3267 Example Street",
                  "452 W Example Ave Glendora Ca 91741"):
            assert ADDRESS_RE.search(s), s

    def test_prose_and_dosages_are_not_addresses(self):
        for s in ("take 1000-1200 mg daily", "in 2019 Dr. Hazen showed",
                  "3 servings per day", "2 capsules with a meal"):
            assert not ADDRESS_RE.search(s), s


class TestGreetingRedaction:
    """Greetings occur below quoted headers and in many shapes (plan §4)."""

    def test_inline_and_dash_and_paren_terminators(self):
        for line in ("Hi Jen, see below for dosing",
                     "Hi Dan - ",
                     "Hey Brian – in answer to your question:",
                     "Hi Derrick (also see my second comment)",
                     "HI Michael,",
                     "Hi marya,",
                     "Hi Kat and Neal"):
            text, _ = scrub_extracted([line])
            assert "[CUSTOMER]" in text, line

    def test_greeting_below_a_quoted_header_is_redacted_as_name(self):
        """The reply (and its greeting) can sit *under* the thread header;
        the old first-line-only rule never reached it."""
        text, rep = scrub_extracted([
            "From: someone@example.org",
            "Sent: Tuesday, June 27, 2023 12:10 PM",
            "To: Experts <experts@example.org>",
            "Subject: Ask the Experts",
            "",
            "Hi Mia,",
            "Thanks for contacting us. The flavor was altered.",
        ])
        assert "Hi Mia" not in text
        assert "[NAME]" in text          # role unknown -> not [CUSTOMER]

    def test_ordinary_prose_is_not_eaten(self):
        for line in ("Hello Thank you for your reply.",
                     "Hi from the team,",
                     "Hi there,",
                     "Hi Team,"):
            text, rep = scrub_extracted([line])
            assert text.strip() == line, line
            assert "greeting_name_residual" not in rep.flags, line

    def test_unredactable_greeting_is_flagged_for_review(self):
        """No terminator and no vocabulary entry -> redacting would risk
        prose; flag instead (unknown names stay a human decision)."""
        _, rep = scrub_extracted(["Hey Jasmine any advice or tips"])
        assert "greeting_name_residual" in rep.flags
        assert rep.residual_pii_flag is True

    def test_good_morning_residual_flags(self):
        # the residual check must cover the same leads as the redact
        # rules — "Good morning <name>" without a terminator used to
        # pass silently (review fix)
        _, rep = scrub_extracted(["Good morning Jasmine any advice"])
        assert "greeting_name_residual" in rep.flags
        assert rep.residual_pii_flag is True

    def test_my_friend_is_not_pii_and_does_not_flag(self):
        """Corpus-attested false positive (owner disposition 2026-09-02):
        'Hello my friend!' carries no name."""
        text, rep = scrub_extracted(["Hello my friend! Hope all is great."])
        assert text == "Hello my friend! Hope all is great."
        assert "greeting_name_residual" not in rep.flags

    def test_bare_greeting_does_not_reach_across_the_newline(self):
        """'Hello' alone on a line must not flag the next line's first word
        (the old residual check's \\s+ crossed newlines — corpus false
        positive, owner disposition 2026-09-02)."""
        _, rep = scrub_extracted(["Hello", "how are you doing today?"])
        assert "greeting_name_residual" not in rep.flags
        assert rep.residual_pii_flag is False


class TestLooseGreetingNames:
    """Names in GREETING_NAME_TOKENS are redacted without a terminator
    (owner disposition 2026-09-02: every corpus greeting residual was a real
    name — no terminator, honorific prefix, lowercase, or slash-joined).
    Lines are built from the vocabulary constant itself so no real name is
    pasted into the test source."""

    NAME, OTHER = sorted(GREETING_NAME_TOKENS)[0], sorted(GREETING_NAME_TOKENS)[-1]

    def test_no_terminator(self):
        text, rep = scrub_extracted(
            [f"Hey {self.NAME} any advice or tips that would be great"])
        assert text == f"Hey [CUSTOMER] any advice or tips that would be great"
        assert rep.greeting_name_redacted
        assert "greeting_name_residual" not in rep.flags

    def test_and_joined_pair_then_prose(self):
        text, _ = scrub_extracted(
            [f"Hello {self.NAME} and {self.OTHER} and good morning!"])
        assert text == "Hello [CUSTOMER] and good morning!"

    def test_lowercase_name(self):
        text, _ = scrub_extracted([f"Hey {self.NAME.lower()} and happy Sunday"])
        assert text == "Hey [CUSTOMER] and happy Sunday"

    def test_honorific_prefix_goes_with_the_name(self):
        text, _ = scrub_extracted(
            [f"Hi Mr. {self.OTHER.capitalize()} and I hope this finds you well."])
        assert text == "Hi [CUSTOMER] and I hope this finds you well."

    def test_slash_joined_pair(self):
        text, _ = scrub_extracted(
            [f"Hi {self.NAME}/{self.OTHER}. I have a client with a question."])
        assert text == "Hi [CUSTOMER]. I have a client with a question."

    def test_vocabulary_token_never_eats_a_longer_word(self):
        """\\b keeps 'kat' from matching inside 'Katherine' — a partial
        match would corrupt the word instead of redacting a name."""
        longer = {t: t.capitalize() + "ine" for t in GREETING_NAME_TOKENS}
        for stem, word in longer.items():
            text, rep = scrub_extracted([f"Hello {word} and thanks for the reply"])
            assert word in text, word
            assert "greeting_name_residual" in rep.flags  # unknown -> flagged

    def test_greeting_below_a_quoted_header_gets_name_not_customer(self):
        lines = [
            "From: someone@example.org",
            "Sent: Tuesday, June 27, 2023 12:10 PM",
            "Subject: Ask the Experts",
            "",
            f"Hello {self.OTHER} any advice would be great",
            "Thanks for contacting us.",
        ]
        text, rep = scrub_extracted(lines)
        assert f"Hello {self.OTHER}" not in text
        assert "Hello [NAME] any advice" in text
        assert rep.redactions["greeting_name_quoted"] == 1


class TestSignoffRedaction:
    """Customer sign-offs in quoted replies (owner disposition 2026-09-05,
    review round 2 — two quoted replies leaked full sign-off names). Names
    below are fabricated (example.com / 555 convention for people too)."""

    HEADER = [
        "From: someone@example.org",
        "Sent: Tuesday, June 27, 2023 12:10 PM",
        "Subject: Ask the Experts",
        "",
    ]

    def test_thanks_plus_full_name(self):
        text, rep = scrub_extracted(self.HEADER + [
            "It was for the chocolate protein",
            "",
            "Thanks,",
            "",
            "Jane Smith",
        ])
        assert "Jane Smith" not in text
        assert text.rstrip().endswith("Thanks,\n\n[NAME]")
        assert rep.redactions["signoff_name"] == 1
        assert rep.residual_pii_flag is False

    def test_delim_plus_regards_plus_credentialed_honorific(self):
        text, rep = scrub_extracted(self.HEADER + [
            "Message: would you recommend this",
            "",
            "-- ",
            "Regards,",
            "",
            "Dr Jane Smith (PhD Org Chem)",
        ])
        assert "Jane Smith" not in text
        assert "Dr [NAME] (PhD Org Chem)" in text
        assert "-- " not in text
        assert rep.redactions["signoff_name"] == 1
        assert rep.dropped["signature_delim"] == 1
        assert rep.residual_pii_flag is False

    def test_expert_region_signoff_is_staff_not_pii(self):
        # above the quoted header: expert sign-offs stay verbatim
        text, rep = scrub_extracted([
            "Thanks,",
            "John",
            "Thanks for contacting us.",
        ] + self.HEADER + ["Question: dosing?"])
        assert "Thanks,\nJohn\nThanks for contacting us." in text
        assert "signoff_name" not in rep.redactions

    def test_expert_note_without_header_untouched(self):
        text, _ = scrub_extracted(["Thanks,", "John"])
        assert "John" in text

    def test_non_name_after_closer_untouched(self):
        text, rep = scrub_extracted(self.HEADER + [
            "Thanks,",
            "we will follow up soon.",
        ])
        assert "we will follow up soon." in text
        assert "signoff_name" not in rep.redactions


class TestInlineSignoffRedaction:
    """One-line customer sign-offs (owner disposition 2026-09-06, Stage 2
    triage round 1 — "Respectfully, Kendra Ferguson" and "Thanks, Matt"
    sit on one line, which the bare-name-next-line rule cannot see)."""

    HEADER = [
        "From: someone@example.org",
        "Sent: Tuesday, June 27, 2023 12:10 PM",
        "Subject: Ask the Experts",
        "",
    ]

    def test_respectfully_plus_full_name(self):
        text, rep = scrub_extracted(self.HEADER + [
            "Thank you in advance! Respectfully, Kendra Ferguson",
        ])
        assert "Kendra Ferguson" not in text
        assert text.rstrip().endswith("Respectfully, [NAME]")
        assert rep.redactions["signoff_name_inline"] == 1
        assert rep.residual_pii_flag is False

    def test_thanks_plus_first_name(self):
        text, rep = scrub_extracted(self.HEADER + [
            "Was the formula changed? Thanks, Matt",
        ])
        assert "Matt" not in text
        assert text.rstrip().endswith("Thanks, [NAME]")
        assert rep.redactions["signoff_name_inline"] == 1

    def test_credential_parenthetical_survives(self):
        text, rep = scrub_extracted(self.HEADER + [
            "Best, Jane Smith (PhD)",
        ])
        assert "Best, [NAME] (PhD)" in text

    def test_three_token_organization_kept_for_review(self):
        # precision over recall: an org-looking tail stays for the LLM flag
        text, rep = scrub_extracted(self.HEADER + [
            "Thanks, Diabetic Support Group",
        ])
        assert "Diabetic Support Group" in text
        assert "signoff_name_inline" not in rep.redactions

    def test_stopword_tail_kept(self):
        text, _ = scrub_extracted(self.HEADER + ["Thanks, everyone"])
        assert "Thanks, everyone" in text

    def test_midline_thanks_untouched(self):
        text, _ = scrub_extracted(self.HEADER + [
            "Thank you, Neal, for the prompt reply",
        ])
        assert "Thank you, Neal, for the prompt reply" in text

    def test_expert_region_and_headerless_notes_untouched(self):
        text, _ = scrub_extracted([
            "Best, Kat Barefield, MS, RDN",
            "Thanks for contacting us.",
        ] + self.HEADER + ["Question: dosing?"])
        assert "Best, Kat Barefield, MS, RDN" in text
        text2, _ = scrub_extracted(["Thanks, Matt"])
        assert "Thanks, Matt" in text2


class TestWroteHeaderRedaction:
    """Quoted attribution headers (owner disposition 2026-09-06, Stage 2
    triage round 1 — "Neal Spruce <[EMAIL]> wrote:" inside replies)."""

    def test_name_plus_bracketed_address(self):
        text, rep = scrub_extracted([
            "Neal Spruce <neal@example.org> wrote:",
        ])
        assert "Neal Spruce" not in text
        assert "[NAME] <[EMAIL]> wrote:" in text
        assert rep.redactions["wrote_header_name"] == 1

    def test_gmail_style_on_date_prefix(self):
        text, _ = scrub_extracted([
            "On Wednesday, June 21, 2023 at 06:44 AM PDT, "
            "Neal Spruce <neal@example.org> wrote:",
        ])
        assert "Neal Spruce" not in text
        assert "wrote:" in text

    def test_lowercase_wrote_untouched(self):
        text, _ = scrub_extracted(["as she wrote: the dose matters"])
        assert "as she wrote: the dose matters" in text


class TestRecipientRedaction:
    def test_display_names_go_but_role_mailboxes_stay(self):
        text, rep = scrub_extracted([
            "To: Experts <experts@example.org>",
            "Cc: Alice Expert <alice@example.org>",
            "To: Porter, Abby",
        ])
        assert "To: Experts <[EMAIL]>" in text
        assert "Alice Expert" not in text
        assert "Porter, Abby" not in text
        assert text.count("[NAME]") == 2
        assert rep.redactions["recipient_name"] == 2


class TestRedactionCounting:
    def test_counts_are_per_occurrence_not_per_document(self):
        _, rep = scrub_extracted([
            "a@example.org b@example.org c@example.org",
            "call 805-555-1414 or 805-555-1415",
        ])
        assert rep.redactions["email"] == 3
        assert rep.redactions["phone"] == 2
