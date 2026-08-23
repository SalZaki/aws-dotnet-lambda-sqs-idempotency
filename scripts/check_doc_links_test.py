#!/usr/bin/env python3
"""Cases for `check-doc-links.py`, run with `python3 scripts/check_doc_links_test.py`.

The other scripts in this directory have no tests, and this one earns them by where it runs. It is a
step in the required `lint` check, so a false positive does not report a broken document — it blocks
every merge until somebody deletes a heading that was correct. The first draft had two: it stripped
underscores out of anchors GitHub keeps, and it silently skipped a link carrying a title and the link
around a badge, which is the one shape where a checker that reports nothing is reporting on nothing.

Both are cases below. What each case pins is a rule of GitHub's rather than a decision of ours, which
is why they are worth a file: the rules are not guessable, and the symptom of getting one wrong is a
green check over an unchecked document, or a red one over a correct link.

Standard library only, and no import of the script by name — `check-doc-links.py` is not an
identifier, and renaming it to make an import work would rename the thing four documents cite.
"""

import contextlib
import importlib.util
import io
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("check-doc-links.py")
_specification = importlib.util.spec_from_file_location("check_doc_links", SCRIPT)
checker = importlib.util.module_from_spec(_specification)
_specification.loader.exec_module(checker)


class SlugTests(unittest.TestCase):
    """What GitHub turns a heading into, which is what a link has to spell."""

    def test_an_underscore_survives_into_the_anchor(self):
        """GitHub drops punctuation but keeps underscores, and stripping them fails a correct link."""
        self.assertEqual(checker.slug("snake_case name"), "snake_case-name")

    def test_punctuation_is_dropped_and_spaces_become_hyphens(self):
        self.assertEqual(checker.slug("TTL Is Cleanup, Not a Boundary"), "ttl-is-cleanup-not-a-boundary")

    def test_markup_does_not_reach_the_anchor(self):
        """A heading is written with backticks and emphasis; neither is in the anchor GitHub emits."""
        self.assertEqual(checker.slug("The `_aws` **envelope**"), "the-_aws-envelope")

    def test_a_heading_that_is_a_link_slugs_its_label(self):
        self.assertEqual(checker.slug("See [the index](README.md) first"), "see-the-index-first")


class AnchorTests(unittest.TestCase):
    def setUp(self):
        self._directory = tempfile.TemporaryDirectory()
        self.root = Path(self._directory.name)
        self.addCleanup(self._directory.cleanup)

    def write(self, name: str, body: str) -> Path:
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(body, encoding="utf-8")
        return path

    def test_repeated_headings_are_numbered_the_way_github_numbers_them(self):
        path = self.write("a.md", "# One\n\n## Steps\n\n## Steps\n\n## Steps\n")
        self.assertEqual(checker.anchors(path), {"one", "steps", "steps-1", "steps-2"})

    def test_a_comment_in_a_code_sample_is_not_a_heading(self):
        """A shell sample opens with `# ...`, and reading it as a heading invents an anchor."""
        path = self.write("a.md", "# One\n\n```bash\n# not a heading\n```\n")
        self.assertEqual(checker.anchors(path), {"one"})


class TargetTests(unittest.TestCase):
    """Which links are seen at all. A link this misses is a link nothing checks."""

    @staticmethod
    def found(text: str) -> list[str]:
        return [target for _, target in checker.targets(text)]

    def test_a_plain_link(self):
        self.assertEqual(self.found("see [the model](cost-model.md#idle)"), ["cost-model.md#idle"])

    def test_a_link_carrying_a_title(self):
        self.assertEqual(self.found('[a](b.md "Some title")'), ["b.md"])

    def test_a_badge_yields_the_link_and_the_image_inside_it(self):
        self.assertEqual(self.found("[![build](badge.svg)](ci.md)"), ["ci.md", "badge.svg"])

    def test_a_bare_image(self):
        self.assertEqual(self.found("![diagram](flow.png)"), ["flow.png"])

    def test_a_label_that_wraps_across_two_lines(self):
        """Prose is hard-wrapped at 100 columns, so this is how a long citation is usually written."""
        self.assertEqual(self.found("see [Cost\nModel](cost-model.md#idle) for the rest"),
                         ["cost-model.md#idle"])


class CheckTests(unittest.TestCase):
    def setUp(self):
        self._directory = tempfile.TemporaryDirectory()
        self.root = Path(self._directory.name)
        self.addCleanup(self._directory.cleanup)

    def tree(self, **documents: str) -> None:
        for name, body in documents.items():
            path = self.root / name.replace("__", "/").replace("_md", ".md")
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(body, encoding="utf-8")

    def failures(self) -> list[str]:
        return checker.check(self.root)

    def only_failure(self) -> str:
        """The one failure a case expects, asserted rather than indexed.

        A case that reads `failures()[0]` reports an `IndexError` on the regression it exists to
        catch, which says nothing about what was expected. This says it.
        """
        failures = self.failures()
        self.assertEqual(len(failures), 1, f"expected one failure, got {failures}")
        return failures[0]

    def test_a_resolving_tree_reports_nothing(self):
        self.tree(
            a_md="# A\n\n[b](b.md#the-heading) and [self](#a) and [dir](sub/)\n",
            b_md="# B\n\n## The heading\n",
            sub__c_md="# C\n",
        )
        self.assertEqual(self.failures(), [])

    def test_a_missing_file_is_named_with_its_line(self):
        self.tree(a_md="# A\n\nsecond line\n\n[gone](nope.md)\n")
        failure = self.only_failure()
        self.assertIn("a.md:5", failure)
        self.assertIn("no such file", failure)

    def test_a_missing_heading_is_reported(self):
        self.tree(a_md="# A\n\n[x](b.md#gone)\n", b_md="# B\n\n## Here\n")
        self.assertIn("no such heading", self.only_failure())

    def test_a_fragment_is_matched_as_written(self):
        """GitHub's anchors are lower case and a browser matches exactly, so this scrolls nowhere."""
        self.tree(a_md="# A\n\n[x](b.md#The-Heading)\n", b_md="# B\n\n## The heading\n")
        self.assertIn("no such heading", self.only_failure())

    def test_an_underscore_anchor_resolves(self):
        """The false positive that would have blocked every merge on a correct link."""
        self.tree(a_md="# A\n\n[x](b.md#snake_case-target)\n", b_md="# B\n\n## snake_case target\n")
        self.assertEqual(self.failures(), [])

    def test_a_titled_link_and_a_badge_are_checked_rather_than_skipped(self):
        self.tree(a_md='# A\n\n[a](nope.md "Title")\n\n[![b](gone.svg)](alsonope.md)\n')
        self.assertEqual(len(self.failures()), 3)

    def test_an_external_link_is_left_alone(self):
        self.tree(a_md="# A\n\n[issue](https://github.com/owner/repo/issues/1) [mail](mailto:a@b.c)\n")
        self.assertEqual(self.failures(), [])

    def test_a_link_inside_a_code_sample_is_not_a_link(self):
        """A fenced block shows how a link is written; the file it names need not exist."""
        self.tree(a_md="# A\n\n```markdown\n[example](never-written.md)\n```\n")
        self.assertEqual(self.failures(), [])

    def test_a_wrapped_link_is_checked_and_reported_on_the_line_its_target_is_on(self):
        """Twenty-five links in this repository were written this way and none of them was checked."""
        self.tree(a_md="# A\n\nsee [the\nmodel](gone.md) here\n")
        failure = self.only_failure()
        self.assertIn("a.md:4", failure)
        self.assertIn("gone.md", failure)

    def test_a_wrapped_link_to_a_renamed_heading_fails(self):
        self.tree(a_md="# A\n\n[Two Idempotency\nScopes](b.md#two-idempotency-scopes)\n",
                  b_md="# B\n\n## Two hashes\n")
        self.assertIn("no such heading", self.only_failure())

    def test_an_anchor_on_a_directory_is_refused(self):
        self.tree(a_md="# A\n\n[x](sub/#heading)\n", sub__c_md="# C\n")
        self.assertIn("anchor on a directory", self.only_failure())

    def test_an_anchor_on_a_file_with_no_headings_is_refused(self):
        self.tree(a_md="# A\n\n[x](notes.txt#top)\n")
        (self.root / "notes.txt").write_text("plain\n", encoding="utf-8")
        self.assertIn("no headings", self.only_failure())

    def test_main_reports_the_failure_it_found(self):
        """The exit code is what the workflow step reads, so it is worth one case of its own."""
        self.tree(a_md="# A\n\n[gone](nope.md)\n")
        stderr = io.StringIO()
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(stderr):
            code = checker.main(self.root)

        self.assertEqual(code, 1)
        self.assertIn("nope.md", stderr.getvalue())

    def test_main_passes_a_clean_tree(self):
        self.tree(a_md="# A\n")
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(checker.main(self.root), 0)


if __name__ == "__main__":
    unittest.main(verbosity=2)
