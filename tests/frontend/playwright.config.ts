import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: '**/*.spec.ts',
  timeout: 30_000,
  use: {
    baseURL: process.env.PLAYWRIGHT_BASE_URL ?? 'http://localhost:5173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    ...devices['Desktop Chrome']
  },
  reporter: [['list'], ['html', { open: 'never' }]],
  webServer: process.env.CI ? {
    command: 'npm run dev --prefix ../../src/frontend',
    url: 'http://127.0.0.1:5173',
    timeout: 120_000,
    reuseExistingServer: false
  } : undefined,
  retries: process.env.CI ? 2 : 0
});
