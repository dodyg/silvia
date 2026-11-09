# Engineering standards

## Code review

Every change needs two approvals before it merges. The main branch is protected,
so no one can push to it directly, and every merge must pass the continuous
integration build.

## Tests and versions

The target for automated test coverage is 70 percent of the lines in each
service. Releases use semantic versioning, and the release notes are published
every Friday afternoon.

## Dependencies

Dependencies are pinned to exact versions. New packages need a short review of
their license, and packages with no release in two years need a replacement plan.
