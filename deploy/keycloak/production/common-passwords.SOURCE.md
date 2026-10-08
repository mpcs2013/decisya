# common-passwords.txt: source and build record

The password blacklist for the production realm only (`passwordBlacklist(common-passwords.txt)`, issue #121, C-02, G3 `docs/security/threat-models/production-identity.md`). The dev realm does not use it.

Approved by Marco on 2026-10-07 (#121, manifest `docs/ai/pipeline/121.md`).

## Upstream

| Item | Value |
| --- | --- |
| Repository | https://github.com/danielmiessler/SecLists |
| File | `Passwords/Common-Credentials/100k-most-used-passwords-NCSC.txt` |
| Pinned commit | `1a7bb9127eca9e6ff2fc0301c597fe6e16a0cb56` (2025-11-19) |
| Upstream size | 99,840 lines, 835,538 bytes |
| Upstream SHA-256 | `c2e5696882c603b76bb67a47ee970897e5a76fc4c3f5547abe3d0ca340c576e0` |
| Origin of the data | the UK NCSC's list of the 100,000 most-used passwords, derived from Have I Been Pwned password frequencies. A frequency list: it holds no account data. |

## Licence and attribution

SecLists is licensed under the MIT License, Copyright (c) 2018 Daniel Miessler. The licence permits use, copying, modification and distribution with this notice:

> MIT License
>
> Copyright (c) 2018 Daniel Miessler
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Build steps

Applied in this order to the upstream file at the pinned commit:

1. Decode hashcat `$HEX[...]` entries (case-insensitive) to their UTF-8 text. Upstream has 2; both decode to fewer than 12 characters and drop out at step 4.
2. Trim surrounding whitespace.
3. Lowercase. Keycloak's blacklist check compares lowercase.
4. Keep entries of 12 to 128 characters, the production policy's `length(12) and maxLength(128)`.
5. De-duplicate and sort. One entry per line, LF line endings, a trailing newline, UTF-8.

## Result

| Item | Value |
| --- | --- |
| Lines | 1,195 (9 contain non-ASCII characters) |
| Bytes | 17,894 |
| SHA-256 | `ed0469a2ad1fd4afa94b53d0878eaaa80018a843d6de3fbfaa94a7c68740c976` |

A test in `deploy/tests` checks the file against this SHA-256. Any change to the list needs a new approval and an update of this record.
