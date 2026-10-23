# Brain Engine Data Migration

This migration is required before deploying the version that removes `IsCodeRule`.

## Transform

For every persisted agent definition:

1. If `Engine` is `Rule` (`1`) and `IsCodeRule` is `true`, set `Engine` to `Code` (`2`).
2. Leave all other `Engine` values unchanged.
3. Remove the `IsCodeRule` field.

`EngineKind.Llm=0` and `EngineKind.Rule=1` retain their serialized values. `EngineKind.Code=2` is the only value for code brains after migration.

## JSON Example

Before:

```json
{ "engine": 1, "isCodeRule": true, "codeLanguage": 0 }
```

After:

```json
{ "engine": 2, "codeLanguage": 0 }
```

## Validation

Before deploying the new runtime, verify:

- no record contains `IsCodeRule` or `isCodeRule`;
- every former code-rule definition has `Engine=2`;
- ordinary rule definitions still have `Engine=1`;
- the number of migrated `Engine=2` records equals the pre-migration count of `Engine=1 && IsCodeRule=true`.

The runtime does not contain a fallback mapping for unmigrated records.
