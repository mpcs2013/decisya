# Changelog

## [0.2.0](https://github.com/mpcs2013/decisya/compare/v0.1.0...v0.2.0) (2026-10-24)


### Features

* **api:** adopt the ostium jwt and backchannel packages with locked restore ([#164](https://github.com/mpcs2013/decisya/issues/164)) ([#177](https://github.com/mpcs2013/decisya/issues/177)) ([4fdad04](https://github.com/mpcs2013/decisya/commit/4fdad04be18479be86e2694f8d40c2192208b8d7))
* **bff:** rate limiting and encrypted data protection key ring ([#122](https://github.com/mpcs2013/decisya/issues/122)) ([#140](https://github.com/mpcs2013/decisya/issues/140)) ([1b5f749](https://github.com/mpcs2013/decisya/commit/1b5f7498efefaa94cadd5b1114a40bb020f4fc60))
* **deploy:** deployable stack with compose publish, caddy edge and file secrets ([#120](https://github.com/mpcs2013/decisya/issues/120)) ([a4fc469](https://github.com/mpcs2013/decisya/commit/a4fc469b18b7b18557e11a088d1036278f5e9c2d))
* **deploy:** deployable stack with compose publish, caddy edge and file secrets ([#120](https://github.com/mpcs2013/decisya/issues/120)) ([de480ba](https://github.com/mpcs2013/decisya/commit/de480ba6142373598e2023a7031943687bba52e8))
* **identity:** production realm, admin mfa and auth event logging ([#121](https://github.com/mpcs2013/decisya/issues/121)) ([#139](https://github.com/mpcs2013/decisya/issues/139)) ([f48c7b1](https://github.com/mpcs2013/decisya/commit/f48c7b187aba64734fdf952e1d907b6c6826ffef))


### Bug Fixes

* **deploy:** image-scan rules, caddy 2.11.7 and ci fixes for the deployable stack ([#120](https://github.com/mpcs2013/decisya/issues/120)) ([8c1368d](https://github.com/mpcs2013/decisya/commit/8c1368dd86f15e103906fee91e0d91a0aa8aadce))
* **deploy:** keycloak admin paths on their own port, nas ingress without allow-lists ([#178](https://github.com/mpcs2013/decisya/issues/178)) ([#182](https://github.com/mpcs2013/decisya/issues/182)) ([ceac939](https://github.com/mpcs2013/decisya/commit/ceac9396a798e0f5741f97cb7e2273f4470fb242))
* **deps:** bump transitive zod to 4.6.7 for GHSA-mrhx-c7xc-m3w5 ([#192](https://github.com/mpcs2013/decisya/issues/192)) ([#193](https://github.com/mpcs2013/decisya/issues/193)) ([9a1aefe](https://github.com/mpcs2013/decisya/commit/9a1aefe29880b3bdba3ff01e254bfd0437d666f6))
* **gates:** review-required check covers renames, leading-zero branches, failed diffs ([#148](https://github.com/mpcs2013/decisya/issues/148)) ([#149](https://github.com/mpcs2013/decisya/issues/149)) ([8d982fb](https://github.com/mpcs2013/decisya/commit/8d982fb182a4e15eda19e3f288bab3bb812637bc))
* **image-scan:** bump postgres, redis and caddy for zlib cve-2026-85091; except caddy curl ([#183](https://github.com/mpcs2013/decisya/issues/183)) ([#184](https://github.com/mpcs2013/decisya/issues/184)) ([1ee709c](https://github.com/mpcs2013/decisya/commit/1ee709c914074bfbe213f07b3037b7dfd40d366c))
* **image-scan:** caddy go item 4, collector 0.163.1, .net 10.0.13, [#120](https://github.com/mpcs2013/decisya/issues/120) renewals ([#143](https://github.com/mpcs2013/decisya/issues/143)) ([#144](https://github.com/mpcs2013/decisya/issues/144)) ([e08f61b](https://github.com/mpcs2013/decisya/commit/e08f61b2eed0b5f3ed21d9171093599be8214d00)), closes [#142](https://github.com/mpcs2013/decisya/issues/142) [#166](https://github.com/mpcs2013/decisya/issues/166)
* **image-scan:** except two unfixed keycloak findings; renew api/bff libc6 ([#190](https://github.com/mpcs2013/decisya/issues/190)) ([#191](https://github.com/mpcs2013/decisya/issues/191)) ([cd7fbc1](https://github.com/mpcs2013/decisya/commit/cd7fbc1ae10e6a067e72a046607f0576d2fcf78f))
* **tooling:** roster ignores untracked local skills ([#135](https://github.com/mpcs2013/decisya/issues/135)) ([#138](https://github.com/mpcs2013/decisya/issues/138)) ([354eb8b](https://github.com/mpcs2013/decisya/commit/354eb8b33c3e80751bc9639ceaa93371668c8c76))

## 0.1.0 (2026-10-04)


### Documentation

* **pipeline:** record the green release dry run for [#119](https://github.com/mpcs2013/decisya/issues/119) ([#128](https://github.com/mpcs2013/decisya/issues/128)) ([4916320](https://github.com/mpcs2013/decisya/commit/4916320aaf79e72c87febb2d294027aa4fbfc97f))


### Continuous Integration

* match the release-please branch name it actually creates ([#119](https://github.com/mpcs2013/decisya/issues/119)) ([#129](https://github.com/mpcs2013/decisya/issues/129)) ([df657a3](https://github.com/mpcs2013/decisya/commit/df657a30844dc9f91ed8e078cb672c0944eca3a6))

## Before 0.1.0

Phase 0 platform work from the repository bootstrap (issue 0.01) up to the release supply chain (#119). From 0.1.0 on, release-please writes this file from Conventional Commits.
