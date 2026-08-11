# FLACTools in AnimeStudio

This directory vendors `teekay/FLACTools` tag `v1.0.1` at commit
`ae5a15670cfe9385e573cafb6827f6253fc5e838`. The upstream project is licensed
under LGPL-2.1; see `COPYING` and `README.upstream.md`.

AnimeStudio carries a focused encoder fix for legal non-standard FLAC sample
rates. Upstream 1.0.1 decodes custom sample-rate frame headers but its writer
rejects every rate outside the fixed lookup table. The local writer selects
FLAC frame-header codes 12, 13, or 14 when the rate fits their compact forms,
and code 0 (inherit from STREAMINFO) for the remaining legal values.
