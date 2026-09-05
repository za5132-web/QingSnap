# Third-party notices

QingSnap uses the following components for optional offline OCR:

- RapidOcrNet — Apache License 2.0 — https://github.com/BobLd/RapidOcrNet
- RapidOCR — Apache License 2.0 — https://github.com/RapidAI/RapidOCR
- PaddleOCR PP-OCRv6 models — Apache License 2.0 — https://github.com/PaddlePaddle/PaddleOCR

The OCR model files are downloaded on demand and stored in the user's local application data directory.

QingSnap also uses the following components for the local screenshot metadata index:

- Microsoft.Data.Sqlite — MIT License — https://www.nuget.org/packages/Microsoft.Data.Sqlite
- SQLite — Public Domain — https://www.sqlite.org/copyright.html

Only screenshot metadata and searchable indexes are stored in SQLite. Image files remain ordinary PNG, JPG, or BMP files.

QingSnap uses the following component for fully offline QR code recognition:

- ZXing.Net — Apache License 2.0 — https://github.com/micjahn/ZXing.Net

QR code recognition runs locally. Screenshot and pinned-image content is not uploaded to any server.
