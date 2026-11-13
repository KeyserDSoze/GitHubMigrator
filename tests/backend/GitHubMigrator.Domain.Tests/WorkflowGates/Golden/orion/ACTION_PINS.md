# Action pins used by the Orion golden workflows

Every SHA was resolved with `git ls-remote https://github.com/<owner>/<repo>.git "refs/tags/<tag>" "refs/tags/<tag>^{}"`
(the peeled commit when the tag is annotated, otherwise the direct one) on the day the corpus was written; a later
tag move or deletion is not detected. They were not checked for known vulnerabilities and the workflows were never run on GitHub.

| action | tag | commit |
| --- | --- | --- |
| `actions/cache` | `v6.1.0` | `55cc8345863c7cc4c66a329aec7e433d2d1c52a9` |
| `actions/checkout` | `v7.0.1` | `3d3c42e5aac5ba805825da76410c181273ba90b1` |
| `actions/download-artifact` | `v8.0.2` | `9000827ccba6bdab643e8b6fd33ac0654aef8333` |
| `actions/setup-dotnet` | `v6.1.0` | `0f4a354dd5547a28c70b7853dbd95d5b67886e73` |
| `actions/setup-node` | `v7.1.0` | `949feb2413d6458794dcd2491c4babbbce0c15c1` |
| `actions/setup-python` | `v7.1.0` | `6d4fd22b20465ccc0f7354476588e8df7c9ca0e0` |
| `actions/upload-artifact` | `v7.0.2` | `cf430e030ddbb5b0abf93d22962f4752f3646cd9` |
| `azure/aks-set-context` | `v5.0.1` | `883bdc8d8d1d0fd87e66cc4ba6f0b618f9eb5281` |
| `azure/login` | `v3.1.0` | `a641126d1b8aa4d1fa005f4f92df94a3a4c4c906` |
| `azure/webapps-deploy` | `v3.0.8` | `02a81bead70021f5284939794bcec79c271ab383` |
| `docker/build-push-action` | `v7.4.0` | `c3c9e263c25d99ce0380d002d59b67737d91b0dc` |
| `docker/login-action` | `v4.8.0` | `8aa5831fb8d48d8f76594c7ddde61ae58019f550` |
| `hashicorp/setup-terraform` | `v4.0.1` | `dfe3c3f87815947d99a8997f908cb6525fc44e9e` |
| `microsoft/setup-msbuild` | `v3.0.0` | `30375c66a4eea26614e0d39710365f22f8b0af57` |
| `sonarsource/sonarqube-quality-gate-action` | `v1.2.1` | `7a5fffe8e523c40e0c740b6bc2712ab503e52efa` |

## Container images (digest of the multi-architecture index)

Resolved with an anonymous registry HEAD request for the manifest (`Accept: application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json`), reading `Docker-Content-Digest`.

| image | digest |
| --- | --- |
| `mcr.microsoft.com/dotnet/sdk:8.0` | `sha256:1b955f179b02df4f0eb5fadedaa898cbe44d620a64aab52eec72c8e91d1355b5` |
| `redis:7` | `sha256:2e3eb11462c4e3759e6972678dd051d150ff54b1e60714b3c33b701260172a90` |
