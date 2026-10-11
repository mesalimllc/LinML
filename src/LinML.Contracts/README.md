# LinML.Contracts

Core interfaces and data models for LinML (Linux ML).

[![NuGet](https://img.shields.io/nuget/v/LinML.Contracts.svg?label=LinML.Contracts&color=blue)](https://www.nuget.org/packages/LinML.Contracts)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](https://github.com/trackdubllc/LinML/blob/main/LICENSE)

`LinML.Contracts` defines provider discovery, hardware abstraction, selection policies, readiness reporting, and inference abstractions with **zero external dependencies on ONNX Runtime**.

---

## Installation

```bash
dotnet add package LinML.Contracts
```

---

## Key Interfaces & Models

- **`IProviderDiscovery`** & **`ProviderDiscoveryReport`**: Honest discovery of compiled-in providers, registered plugins, and hardware accelerators.
- **`IProviderReadinessProbe`** & **`ProviderReadinessReport`**: Detailed readiness diagnostics for providers (CUDA, TensorRT, OpenVINO, etc.).
- **`IInferenceSessionFactory`** & **`IInferenceSession`**: Clean abstractions for lifecycle management and running inference sessions.
- **`InferenceRequest`** & **`InferenceResult`**: Hardware-independent representations of input and output tensors.
- **`ProviderId`**: Strongly typed execution provider identifiers.

---

## Documentation & Repository

For more details, CLI tools, and architecture documentation, visit the [LinML GitHub Repository](https://github.com/trackdubllc/LinML).
