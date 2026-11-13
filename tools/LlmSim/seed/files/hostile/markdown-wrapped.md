Sure! Here is the GitHub Actions workflow you asked for. I converted your Azure Pipeline and kept it as close to the original as I could.

```yaml
name: CI
on:
  push:
    branches: [main]
permissions:
  contents: read
jobs:
  build:
    runs-on: ubuntu-latest
    timeout-minutes: 15
    steps:
      - name: Check out
        uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683 # v4.2.2
        with:
          persist-credentials: false
      - name: Build
        run: dotnet build --configuration Release
```

Let me know if you would like me to add caching, a test matrix or a deployment job. Happy to help!
