# Game compatibility

Memory layouts can change between Ostriv builds. A version label alone is not sufficient to allow a write; Ostriv Studio checks the executable's SHA-256 and rejects untested builds.

| Ostriv version | Executable SHA-256 | Status |
| --- | --- | --- |
| Alpha 5, patch 9, hotfix 58 (`0.5.9.58`) | `D66B8F19FFCA57845B3085F94E01B8CA7685D02211E7EDADAECF579591B6D771` | Experimental support for the limited features listed in [FEATURE-COVERAGE.md](FEATURE-COVERAGE.md). Tests used a disposable settlement; long-term stability is not established. |
| Other versions or different executable hash | Any other hash | Read-only inspection may be available; game-memory writes are blocked until separately researched and tested. |

The app does not contain the game or the original resource editor. It is an independent community tool and is not affiliated with Ostriv or the original editor's author.
