# Orion web

React storefront. CI runs on Node 18, 20 and 22; deployment copies the bundle to the staging hosts over SSH.

```
npm ci
npm run lint
npm test
npm run build
```

`public/images/hero.png` and `data/catalog-snapshot.bin` are binary assets (the snapshot is above 1 MB on purpose).
