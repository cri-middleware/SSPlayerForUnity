# Third-Party Notices

This file contains the license notices for third-party libraries used in **SpriteStudio Player for Unity**.

We would like to thank the authors and contributors of these open-source projects.

---

## FlatBuffers

This project depends on FlatBuffers for binary data serialization.

Copyright (c) 2014 Google Inc.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.

---

## SpriteStudio-SDK

This project integrates `SpriteStudio-SDK` (`libssruntime` and `libssconverter`) via FFI bindings.

Copyright (c) CRI Middleware Co., Ltd. All rights reserved.

`SpriteStudio-SDK` is licensed under the BSD-3-Clause License.

### Rust Runtime Dependencies

`libssruntime` and `libssconverter` are written in Rust and depend on various third-party open-source crates.

**License Location:**
The license documents for the Rust dependencies ship with the native libraries. In the released package they are `Runtime/Plugins/THIRD-PARTY-LICENSES.ssruntime.md` and `Editor/Plugins/THIRD-PARTY-LICENSES.ssconverter.md`, each beside the SDK's `LICENSE.md`.

**Distribution Note:**
If you distribute compiled Unity applications or Unity packages that include `libssruntime`, please ensure you also bundle and distribute the license files (`THIRD-PARTY-LICENSES.ssruntime.md` and `LICENSE.md`) located in the package's `Runtime/Plugins/` to comply with the open-source licenses of the underlying Rust crates.
