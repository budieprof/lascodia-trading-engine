# Agent Review Feedback

This document captures repo-specific feedback for how agent reviews should be presented.

## Findings Policy

When the agent performs a review and reports findings:

- Do not cap the number of findings shown.
- List all findings discovered during the review.
- Do not stop after a fixed number of items, even if the list is long.
- The findings section should be exhaustive relative to what was identified in that review pass.

## Rationale

For this repository, partial reporting is considered less useful than a complete findings list. If the agent detects issues, the expectation is full disclosure of all identified findings rather than a limited or curated subset.

## Tone Guidance

When the implementation under review is strong, say that plainly.

- Keep findings first, but do not let a conservative review tone obscure genuine strengths.
- If the code is robust, intelligent, or comprehensive overall, state that clearly before or after the findings.
- Avoid overly hedged phrasing when the overall assessment is positive and only a small number of issues were found.
- Use a direct, appreciative tone that still preserves honesty about risks and defects.
