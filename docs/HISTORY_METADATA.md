# QingSnap history metadata

QingSnap keeps screenshot image bytes in the configured history directory as ordinary PNG, JPG, or BMP files. The SQLite database is a rebuildable metadata index and never contains image blobs.

## Database location

- Standard mode: `%LOCALAPPDATA%\QingSnap\history-metadata.db`
- Portable mode: `<QingSnap directory>\Data\history-metadata.db`

The path is based on `AppSettingsService.DataDirectory`, so enabling `portable.flag` keeps the database with the portable data directory even when a custom screenshot history directory is configured.

## Schema version 1

- `SchemaInfo`: one-row schema version and migration timestamp.
- `HistoryItems`: file path, capture time, dimensions, file size, format, long-capture/favorite flags, OCR text and state, source process/window, monitor identity, physical capture rectangle, SHA-256 image hash, and creation/update timestamps.
- `Tags`: normalized reusable user-tag dictionary.
- `HistoryItemTags`: many-to-many relationship between screenshots and tags.

Tag names never alter screenshot file names. Removing a tag from a screenshot deletes only the relationship, while deleting a screenshot cascades its tag relationships without touching other screenshots or tag definitions.

All values are written with parameterized SQL. Batched writes use one transaction, WAL mode, a busy timeout, and a single background writer.

## Migration and compatibility

At startup QingSnap scans the existing history directory in the background. Existing `.qingsnap-favorites.json` entries and `.qingsnap-index/*.txt` OCR indexes are imported without changing or deleting those legacy files. New favorite and OCR updates continue writing both formats during the compatibility period.

The history window continues to discover screenshots from the image directory, so it remains usable while migration is still running. New captures queue metadata persistence after the image file has been safely encoded.

## Recovery

If SQLite reports a corrupt database during initialization, QingSnap renames the database and its WAL sidecars with a `.corrupt-<timestamp>` suffix, creates a clean schema, and rebuilds metadata from the image directory in the background. Missing image files are removed from the index during history refresh. The original screenshot files are never deleted as part of database recovery.
