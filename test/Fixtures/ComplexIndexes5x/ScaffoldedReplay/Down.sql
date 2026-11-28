START TRANSACTION;
DROP INDEX ledger.ux_entry_kind_title;

DROP INDEX ledger.ix_entry_note_lower;

CREATE INDEX ix_entry_kind_lower ON ledger.entries ((lower("Kind")) COLLATE "C" text_pattern_ops);

CREATE UNIQUE INDEX ux_entry_kind_title ON ledger.entries (("Kind"), (upper("Title")) COLLATE "C" text_pattern_ops DESC NULLS LAST) INCLUDE ("Rank", "Amount_Currency") NULLS NOT DISTINCT WITH (deduplicate_items=false, fillfactor=70) WHERE "Rank" > 0;

COMMIT;

CREATE INDEX CONCURRENTLY ix_entry_code_upper ON ledger.entries ((upper("Code")));

DELETE FROM "__EFMigrationsHistory"
WHERE "MigrationId" = '20260102000000_Changed';

START TRANSACTION;
DROP TABLE ledger.entries;

DELETE FROM "__EFMigrationsHistory"
WHERE "MigrationId" = '20260101000000_Initial';

COMMIT;

