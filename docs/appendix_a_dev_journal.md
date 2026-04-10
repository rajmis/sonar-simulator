
## Appendix A: Development Journal — What We Tried & What We Learned

This appendix documents the implementation journey chronologically, including approaches that failed and the reasoning behind course corrections. Intended as institutional memory for future development.

---

### A.1 UDP Chunking (Attempted, Abandoned)

**What we built:** The initial network layer used UDP on localhost with two ports — Unity sent on port 9000, Python replied on port 9001. Since the 553 KB ping payload exceeds UDP's ~65 KB datagram limit, we implemented a chunking system: payloads were split into 60 KB chunks with a 12-byte chunk header (pingID, chunkIndex, totalChunks, payloadLength). Python had a `PingAssembler` class that collected chunks per pingID and reassembled when all arrived.

**What went wrong:** At 15 Hz with 10 chunks per ping, the OS socket receive buffer (~416 KB on Linux, despite requesting 4 MB via `SO_RCVBUF`) consistently overflowed. Python received only 5–7 out of 10 chunks per ping. We tried:

- Increasing `SO_RCVBUF` to 4 MB — Linux kernel capped it at ~416 KB (the `net.core.rmem_max` system limit)
- Reducing socket timeout from 0.5s to 10ms and draining all available datagrams in a tight loop — improved from ~14 to ~174 completed pings, but still heavy chunk loss
- Stale ping garbage collection to prevent memory leaks from incomplete pings

**Why we switched to TCP:** TCP eliminates the entire chunking/reassembly problem. A single `sendall()` of 553 KB just works — TCP handles segmentation, flow control, and guaranteed delivery internally. The TCP code is actually *simpler* than the UDP code (no chunk headers, no assembler, no GC). Zero packet loss from first run.

**Lesson:** UDP on localhost is not free — even loopback traffic goes through OS socket buffers that can overflow under burst load. TCP is the right default for same-machine IPC with large payloads.

---

### A.2 Two Separate Network Components → One (Refactored)

**What we built first:** `SonarSender.cs` (UDP send, port 9000) and `SonarReceiver.cs` (UDP receive on background thread, port 9001) as separate MonoBehaviours.

**Why we merged them:** TCP uses a single bidirectional connection. Having two components share a `TcpClient` adds unnecessary synchronisation complexity. `SonarNetworkClient.cs` owns the connection, sends on the main thread, receives on a background thread, and exposes the same `OnImageReady` event that `SonarReceiver` had. Cleaner ownership, simpler Inspector setup (one component instead of two).

---

### A.3 Beam Direction Mismatch (Debugged)

**Symptom:** First working render showed returns in completely wrong positions — the pillar appeared smeared across the image and the seabed geometry didn't match the scene.

**Root cause:** The new `SonarRaycaster` used a forward-facing spherical coordinate beam pattern (`sin/cos` decomposition into x/y/z), while the legacy `SonarGPUEmitter` used a rotation-chain approach: `Quaternion.AngleAxis(fan) * Quaternion.AngleAxis(vert) * Quaternion.AngleAxis(tilt) * Vector3.down`. The downward-looking fan orientation and the specific rotation order are fundamental to multibeam sonar geometry.

**Fix:** Copied the exact rotation chain from `SonarGPUEmitter.GetBeamDirection()` into the raycaster's `PrecomputeRayDirections()`. Both now produce identical beam patterns.

**Lesson:** When replacing a working system, match its geometry exactly before improving it. Don't re-derive from first principles what an existing implementation already encodes correctly.

---

### A.4 Stripe Artifacts from Additive Blending (Debugged)

**Symptom:** Seabed returns appeared as alternating bright/dark horizontal bands instead of a solid line.

**Root cause:** Range binning used `np.add.at` to accumulate sub-beam contributions into range bins with a pulse envelope spread of ±8 bins. Where pulse envelopes from adjacent sub-beams overlapped additively, some bins got double/triple intensity while gaps between them stayed low, creating a repeating stripe pattern.

**Fix:** Switched to `np.maximum.at` — each bin takes the maximum contribution rather than the sum. This matches the legacy `Mathf.Max` approach in `SonarGPUEmitter.BuildIntensityImage()` and produces clean, solid target returns.

**Lesson:** The legacy code's choice of max-blending over additive blending wasn't arbitrary — it was solving exactly this visual artifact.

---

### A.5 Per-Ping Normalisation Crushing Weak Returns (Debugged)

**Symptom:** When the sonar moved close to the steel pillar, the seabed return completely disappeared. When far away, both were visible.

**Root cause:** The processing pipeline normalised the output image with `image /= max_val`. The sonar equation produces physically-scaled intensities — a steel pillar at 2m range has intensity ~10¹⁷ while the sand seabed at 8m has ~10¹⁴. Dividing by the pillar's intensity pushed the seabed to ~0.001, invisible in the colour map.

**Fix:** Replaced per-ping normalisation with fixed 60 dB dynamic range window in dB space:
```
ceiling = max(10 * log10(intensity))
floor = ceiling - 60
normalised = (image_dB - floor) / 60
```
Both targets now remain visible because they're within the 60 dB display window. This matches how real sonar displays work — operators set a fixed gain window, not auto-scale per ping.

**Lesson:** Per-ping normalisation is almost never correct for sonar imagery. Fixed dynamic range is the standard for a reason.

---

### A.6 Noise Bleeding into Shadow Zones (Debugged)

**Symptom:** After fixing normalisation, the shadow region behind the pillar and below the seabed had visible blue noise instead of being nearly black.

**Root cause:** Noise (receiver noise, band noise, Gaussian noise, speckle) was applied globally to the entire image, including shadow zones that should be acoustically dead.

**Fix:** Built an `active_mask` array marking bins before the shadow start per beam. Bulk noise is applied only where `active_mask` is True. Shadow zones receive only trace noise (0–0.005) to avoid perfect mathematical blackness while staying nearly invisible.

**Lesson:** Noise application regions matter as much as noise parameters. Shadow zones should be treated as a distinct acoustic regime.

---

### A.7 ScriptableObject Serialisation Gotcha (Recurring)

**Hit this twice:** Once during initial SonarConfig setup (documented in prior work), and again when adding the new acoustic fields (`speedOfSound`, `sourceLevel_dB`, `pulseLength_s`, `frequency_Hz`). The existing `DefaultConfig.asset` retained its old serialised values (all zeros for new fields) even though the C# class had non-zero defaults.

**Symptom:** `NullReferenceException` in `SonarSender` and `SonarReceiver` at lines reading config fields — but the config reference wasn't null, the *values* were zero/default.

**Fix:** Manually set the new field values in the Unity Inspector on the existing asset.

**Lesson:** This is a fundamental Unity behaviour, not a bug. ScriptableObject `.asset` files are YAML-serialised snapshots. New fields get the type's default (0, null) not the C# initialiser value. Always check the Inspector after adding fields.

---

### A.8 SonarBridge Not Running (Missed Component)

**Symptom:** Python was processing pings and sending responses, `SonarNetworkClient.pingsProcessed` counter was incrementing, but `SonarDisplay` showed nothing. No `UpdateFromTexture` debug logs appeared.

**Root cause:** `SonarBridge` component had not been added to the sonar GameObject (`SonarHeadGPU`). Without it, `SonarNetworkClient.OnImageReady` had no subscribers, and images were dequeued but never forwarded to the display.

**Fix:** Added `SonarBridge` component to `SonarHeadGPU`. The `RequireComponent` attributes on SonarBridge only enforce presence *if SonarBridge itself is added* — they don't auto-add SonarBridge.

**Lesson:** When debugging a pipeline, verify every link in the chain. The `[RequireComponent]` pattern ensures co-dependencies but doesn't ensure the orchestrator itself gets added.

---

### A.9 RFloat Texture vs RGBA32 Display (Debugged)

**Symptom:** After fixing the bridge, the fan display rendered but was entirely black (except range rings).

**Root cause:** `SonarNetworkClient` creates an `RFloat` texture (single-channel float32). The initial `UpdateFromTexture` implementation passed this directly to the fan material. But the fan shader and colour mapping expected RGBA32 data. The legacy `OnPing` path applied `colourMap.Evaluate()` to convert float→colour, but the new path skipped this step.

**Fix:** `UpdateFromTexture` now reads raw float data via `GetPixelData<float>()`, applies the same `colourMap.Evaluate()` gradient, and writes to a separate RGBA32 texture for the shader.

**Lesson:** The texture format contract between the network layer and the display layer needs explicit conversion. Raw sensor data (float32 intensity) and display data (RGBA colour) are fundamentally different representations.
