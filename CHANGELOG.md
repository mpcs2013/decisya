# Changelog

## [0.2.0](https://github.com/mpcs2013/decisya/compare/v0.1.0...v0.2.0) (2026-10-31)


### Features

* **api:** adopt the ostium jwt and backchannel packages with locked restore ([#164](https://github.com/mpcs2013/decisya/issues/164)) ([#177](https://github.com/mpcs2013/decisya/issues/177)) ([4fdad04](https://github.com/mpcs2013/decisya/commit/4fdad04be18479be86e2694f8d40c2192208b8d7))
* **bff:** rate limiting and encrypted data protection key ring ([#122](https://github.com/mpcs2013/decisya/issues/122)) ([#140](https://github.com/mpcs2013/decisya/issues/140)) ([1b5f749](https://github.com/mpcs2013/decisya/commit/1b5f7498efefaa94cadd5b1114a40bb020f4fc60))
* **deploy:** build and publish a self-built caddy image ([#188](https://github.com/mpcs2013/decisya/issues/188)) ([#189](https://github.com/mpcs2013/decisya/issues/189)) ([61114c3](https://github.com/mpcs2013/decisya/commit/61114c38cb66f0fb49d4daea1e7a31020ed123b5))
* **deploy:** deployable stack with compose publish, caddy edge and file secrets ([#120](https://github.com/mpcs2013/decisya/issues/120)) ([a4fc469](https://github.com/mpcs2013/decisya/commit/a4fc469b18b7b18557e11a088d1036278f5e9c2d))
* **deploy:** deployable stack with compose publish, caddy edge and file secrets ([#120](https://github.com/mpcs2013/decisya/issues/120)) ([de480ba](https://github.com/mpcs2013/decisya/commit/de480ba6142373598e2023a7031943687bba52e8))
* **deploy:** switch the stack to the self-built caddy image ([#225](https://github.com/mpcs2013/decisya/issues/225)) ([#226](https://github.com/mpcs2013/decisya/issues/226)) ([06f4455](https://github.com/mpcs2013/decisya/commit/06f445569b7945849bf741c5d35f1d786ded048d))
* **identity:** production realm, admin mfa and auth event logging ([#121](https://github.com/mpcs2013/decisya/issues/121)) ([#139](https://github.com/mpcs2013/decisya/issues/139)) ([f48c7b1](https://github.com/mpcs2013/decisya/commit/f48c7b187aba64734fdf952e1d907b6c6826ffef))
* **image-scan:** adr-0022 delta pr gate, gate-change comparison and deploy freshness gate ([#222](https://github.com/mpcs2013/decisya/issues/222)) ([#224](https://github.com/mpcs2013/decisya/issues/224)) ([4acf8f8](https://github.com/mpcs2013/decisya/commit/4acf8f8f1c8aad0f2275c16ce2a0905cc3e8f031))


### Bug Fixes

* **claude:** deny agent writes to .user, obj and node_modules files ([#218](https://github.com/mpcs2013/decisya/issues/218)) ([#220](https://github.com/mpcs2013/decisya/issues/220)) ([144e629](https://github.com/mpcs2013/decisya/commit/144e629a41a3aed807e20a0e5dc042c95e099226))
* **deploy:** image-scan rules, caddy 2.11.7 and ci fixes for the deployable stack ([#120](https://github.com/mpcs2013/decisya/issues/120)) ([8c1368d](https://github.com/mpcs2013/decisya/commit/8c1368dd86f15e103906fee91e0d91a0aa8aadce))
* **deploy:** keycloak admin paths on their own port, nas ingress without allow-lists ([#178](https://github.com/mpcs2013/decisya/issues/178)) ([#182](https://github.com/mpcs2013/decisya/issues/182)) ([ceac939](https://github.com/mpcs2013/decisya/commit/ceac9396a798e0f5741f97cb7e2273f4470fb242))
* **deps:** bump transitive zod to 4.6.7 for GHSA-mrhx-c7xc-m3w5 ([#192](https://github.com/mpcs2013/decisya/issues/192)) ([#193](https://github.com/mpcs2013/decisya/issues/193)) ([9a1aefe](https://github.com/mpcs2013/decisya/commit/9a1aefe29880b3bdba3ff01e254bfd0437d666f6))
* **gates:** review-required check covers renames, leading-zero branches, failed diffs ([#148](https://github.com/mpcs2013/decisya/issues/148)) ([#149](https://github.com/mpcs2013/decisya/issues/149)) ([8d982fb](https://github.com/mpcs2013/decisya/commit/8d982fb182a4e15eda19e3f288bab3bb812637bc))
* **image-scan:** bump postgres, redis and caddy for zlib cve-2026-85091; except caddy curl ([#183](https://github.com/mpcs2013/decisya/issues/183)) ([#184](https://github.com/mpcs2013/decisya/issues/184)) ([1ee709c](https://github.com/mpcs2013/decisya/commit/1ee709c914074bfbe213f07b3037b7dfd40d366c))
* **image-scan:** caddy go item 4, collector 0.163.1, .net 10.0.13, [#120](https://github.com/mpcs2013/decisya/issues/120) renewals ([#143](https://github.com/mpcs2013/decisya/issues/143)) ([#144](https://github.com/mpcs2013/decisya/issues/144)) ([e08f61b](https://github.com/mpcs2013/decisya/commit/e08f61b2eed0b5f3ed21d9171093599be8214d00)), closes [#142](https://github.com/mpcs2013/decisya/issues/142) [#166](https://github.com/mpcs2013/decisya/issues/166)
* **image-scan:** clear the 2026-10-26 drift with the caddy bridge ([#195](https://github.com/mpcs2013/decisya/issues/195), [#187](https://github.com/mpcs2013/decisya/issues/187), [#212](https://github.com/mpcs2013/decisya/issues/212)) ([#215](https://github.com/mpcs2013/decisya/issues/215)) ([8930453](https://github.com/mpcs2013/decisya/commit/8930453866c94e3f2b65f223c4ee9c8cc9b1318b))
* **image-scan:** except two unfixed keycloak findings; renew api/bff libc6 ([#190](https://github.com/mpcs2013/decisya/issues/190)) ([#191](https://github.com/mpcs2013/decisya/issues/191)) ([cd7fbc1](https://github.com/mpcs2013/decisya/commit/cd7fbc1ae10e6a067e72a046607f0576d2fcf78f))
* **tests:** bash-based tests never start the wsl launcher on windows ([#217](https://github.com/mpcs2013/decisya/issues/217)) ([#219](https://github.com/mpcs2013/decisya/issues/219)) ([6fc8118](https://github.com/mpcs2013/decisya/commit/6fc811896467d0f920b95e3e8dc35b33379e77e0))
* **tooling:** roster ignores untracked local skills ([#135](https://github.com/mpcs2013/decisya/issues/135)) ([#138](https://github.com/mpcs2013/decisya/issues/138)) ([354eb8b](https://github.com/mpcs2013/decisya/commit/354eb8b33c3e80751bc9639ceaa93371668c8c76))

## 0.1.0 (2026-10-04)


### Documentation

* **pipeline:** record the green release dry run for [#119](https://github.com/mpcs2013/decisya/issues/119) ([#128](https://github.com/mpcs2013/decisya/issues/128)) ([4916320](https://github.com/mpcs2013/decisya/commit/4916320aaf79e72c87febb2d294027aa4fbfc97f))


### Continuous Integration

* match the release-please branch name it actually creates ([#119](https://github.com/mpcs2013/decisya/issues/119)) ([#129](https://github.com/mpcs2013/decisya/issues/129)) ([df657a3](https://github.com/mpcs2013/decisya/commit/df657a30844dc9f91ed8e078cb672c0944eca3a6))

## Before 0.1.0

Phase 0 platform work from the repository bootstrap (issue 0.01) up to the release supply chain (#119). From 0.1.0 on, release-please writes this file from Conventional Commits.
