# Contracts

A contract is the agreed interface between workstreams: types, encodings, file formats, rules and the known-answer vectors that pin them. Each contract has one text here, written in full by its owner (dev plan, section 5). The machine-readable parts live next to the code they govern, and the text says where.

- **How a contract changes:** `ownership-and-contract-change.md`. In short, contracts are drafts until the tag `contracts-v1`. After the tag, a contract file changes only through a PR labelled `contract-change`, which adds an entry to `CHANGELOG.md` and updates the vectors. The hygiene job enforces the label and the entry.
- **The record of every version** is `CHANGELOG.md`. Any family may append its entry at the end; only S0 rewrites lines or inserts them elsewhere.
- **Freeze tags.** The plan names `contracts-v1` for the S1, R4a, O2 and C1 contracts at the end of wave 1, and `contracts-v1.1` for R4b's SPIR-V binding (ShaderBuildAndSourceConvention) after R1's ADR. Mechanically, a tag freezes **every** contract file on `main` when it is set, drafts included. So the integrator sets `contracts-v1` only once every wave-1 PR (#2 to #12) and the cross-cutting fixes of `int/wave1-fixes` are on `main`: O1's, T1's, R1's and R3's texts then join the tag instead of needing `contract-change` PRs afterwards. `contracts-v1.1` freezes the R4b and R1 snapshot: R4b's three texts at v1.1 (ShaderBuildAndSourceConvention, KernelAbiAndCatalog and HeapLayoutAndTensorTable) and R1's VulkanHeapRecipe v1.1; both tags can point at the same commit. Later versions, such as ExecutionClass v1.1, DataRootAndToolchain v1.1 and ShaderBuildAndSourceConvention v1.2 from `int/linux-host`, are not part of either tag.
- **Until the tags: frozen by convention.** This departs from the plan's wave-1 exit ("R4a merged: tag contracts-v1"), and wave 2 runs before the tags exist, so the hygiene job cannot enforce the freeze yet. Until `contracts-v1` is set, every wave-1 contract text (every file this index lists as v1 or v1.1) is treated as frozen: workstream branches do not edit it, and only an `int/` commit changes it, with a CHANGELOG entry and a routed note to every consumer. A workstream that needs a change asks the integrator in its PR. The user may instead merge and tag before any wave-2 PR lands (PR #14, "For the user").

## Index

Status as of 2026-10-18. "v1", "v1.1" or "v1.2" means written in full and in review or merged; "planned" means not written yet. Every wave-1 text is on its workstream PR (#4 to #12) and combined on the integration preview `int/wave1` (PR #13), with the cross-cutting fixes of `int/wave1-fixes`; none is on `main` or tagged yet. The integrator updates this table as PRs land.

| Contract | Owner | Text | Status |
| --- | --- | --- | --- |
| ProjectGraph | S0 | `project-graph.md` | v1 (PR #2; hygiene enforcement in PR #9) |
| TestTiers | S0 | `test-tiers.md` | v1 (PR #2) |
| DataRootAndToolchain | S0 | `data-root-and-toolchain.md` | v1.2 (PR #2; tokenizer and SDK entries in PR #9; Linux DXC in `int/linux-host`; native vkd3d-proton in `int/linux-d3d12`) |
| AgentPermissions | S0 | `agent-permissions.md` | v1 (PR #2; enforcement in PR #9) |
| OwnershipAndContractChange | S0 | `ownership-and-contract-change.md` | v1 (PR #9) |
| LrmCore | S1 | `lrm-core.md` | v1 (PR #3) |
| TelemetryFacade | S1 | `telemetry-facade.md` | v1 (PR #3) |
| ExecutionClass | S1 (type); R4a (GPU fill rule) | `execution-class.md` | v1.2 (PR #3; Linux host in `int/linux-host`; vkd3d-proton class in `int/linux-d3d12`) |
| RunLog | T1 | `run-log.md` | v1 (PR #8; `run.*` reserved in TelemetryFacade) |
| ReportTemplates | T1 | `report-templates.md` | v1 (PR #8; parity and kernels reading rules extended in `int/wave1-fixes`) |
| HostTensorAndSafetensors | O2 | `host-tensor.md` | v1 (PR #11) |
| CpuReferenceSemantics | O2 | `cpu-reference.md` | v1 (PR #11) |
| ComparatorsAndGoldenReader | O2 | `comparators.md` | v1 (PR #11) |
| KernelBounds | O2 | `kernel-bounds.md` | v1 (PR #11) |
| ToleranceSpec | O2 (types); G2 (numbers) | `tolerance-spec.md` | v1 (PR #11; numbers in wave 2) |
| ReferenceOracles | O1 | `reference-oracles.md` | v1 (PR #4; RunLog-format telemetry in `int/wave1-fixes`) |
| GoldenSet | O1 (format); G2 (Qwen content) | `golden-set.md` | v1 (PR #4) |
| ActivationNames | O1 | `activation-names.md` | v1 (PR #4) |
| HeapLayoutAndTensorTable | R4 | `heap-and-tensor-table.md` | v1.1 (R4a PR #10; R4b PR #12) |
| KernelAbiAndCatalog | R4 | `kernel-abi.md` | v1.1 (R4a PR #10; R4b PR #12) |
| GpuBackendInterface | R4 | `gpu-backend.md` | v1.1 (PR #10; Linux D3D12 in `int/linux-d3d12`) |
| ShaderBuildAndSourceConvention | R4 (SPIR-V `LRM_BUF`: R4b) | `shader-build.md` | v1.2 (R4a PR #10; R4b PR #12; the Linux DXC in `int/linux-host`) |
| BatchInvariance | R4 | `batch-invariance.md` | v1 (PR #10) |
| VulkanHeapRecipe | R1 | `vulkan-heap-recipe.md` | v1.1 (R1 PR #5; section 7 amended in `int/wave1-fixes`) |
| VulkanInteropApi | R2 | `vulkan-interop.md` | planned |
| D3D12ComIdiom | R3 | `d3d12-com-idiom.md` | v1.1 (PR #6; native Linux route in `int/linux-d3d12`) |
| ClusterProtocol | C1 | `cluster.md` | v1 (PR #7) |
| ConformanceHarness | E1 | `conformance-harness.md` | v1 draft (E1, wave 2, PR #17) |
| TokenizerApi | M1 | `tokenizer.md` | v1 (PR #18; ADR M1-0001) |
| ForwardOpsAndQwenModel | M2 | `qwen-model.md` | planned (wave 2) |
| GpuExecutor | X1 | `gpu-executor.md` | planned (wave 3) |
| StagePipeline | C2 | `stage-pipeline.md` | planned (wave 3) |
| Gate1Evidence | I1, I2, I3 | `gate1-evidence.md` | planned (wave 4) |

## Writing a contract text

- **Heading:** `# <Name> contract`.
- **First line:** `Owner: …. Status: vN. Consumers: ….`, plus where the code and tests are.
- **Body:** the rules, as short statements a reviewer can check one by one.
- **Vectors:** a section with known-answer vectors wherever there is an encoding, a hash, a layout or a decision table. They are computed independently of the code under test (for example with `sha256sum`), and the tests check them.
- **Verification:** a section that names the tests and what they prove.
