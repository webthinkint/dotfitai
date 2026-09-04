"""Stage 0 scrub tests (pure functions — fast, no filesystem for the core)."""

from __future__ import annotations

from qa_pipeline.extract import extract_docx
from qa_pipeline.scrub import (
    ADDRESS_RE, CARD_RE, EMAIL_RE, PHONE_RE, PROFILE_URL_RE, SSN_RE,
    scrub_extracted,
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
        """No terminator -> redacting would risk prose; flag instead."""
        _, rep = scrub_extracted(["Hey Neal any advice or tips"])
        assert "greeting_name_residual" in rep.flags
        assert rep.residual_pii_flag is True


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


class TestFilenameNameFlag:
    def test_filename_repeating_a_redacted_name_flags(self):
        _, rep = scrub_extracted(
            ["Hi Jasmine,", "See the program note."],
            filename="update supp program connect -jasmine")
        assert "filename_contains_redacted_name" in rep.flags
        assert rep.residual_pii_flag is True

    def test_unrelated_filename_does_not_flag(self):
        _, rep = scrub_extracted(
            ["Hi Jasmine,", "See the program note."],
            filename="creatine loading protocol FAQ")
        assert "filename_contains_redacted_name" not in rep.flags


class TestRedactionCounting:
    def test_counts_are_per_occurrence_not_per_document(self):
        _, rep = scrub_extracted([
            "a@example.org b@example.org c@example.org",
            "call 805-555-1414 or 805-555-1415",
        ])
        assert rep.redactions["email"] == 3
        assert rep.redactions["phone"] == 2
