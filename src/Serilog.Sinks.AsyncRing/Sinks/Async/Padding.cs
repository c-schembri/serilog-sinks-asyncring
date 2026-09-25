// Copyright © Serilog.Sinks.AsyncRing Contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Runtime.InteropServices;

namespace Serilog.Sinks.Async;

// Values that are hammered by different threads each get a cache line to themselves, so a write to
// one doesn't invalidate the others (false sharing).

[StructLayout(LayoutKind.Explicit, Size = 128)]
struct PaddedLong
{
    [FieldOffset(64)] public long Value;
}

[StructLayout(LayoutKind.Explicit, Size = 128)]
struct PaddedInt
{
    [FieldOffset(64)] public int Value;
}
