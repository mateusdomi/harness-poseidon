import { test } from '@playwright/test';
import { adoptProfile, activateProject } from './journeys';

test.describe('final mission probe', () => {
  test.beforeEach(async ({ page }) => {
    await adoptProfile(page, 'Mateus');
  });

  test('dashboard timing', async ({ page }) => {
    const start = Date.now();
    await page.goto('/');
    await page.waitForLoadState('networkidle');
    const elapsed = Date.now() - start;
    console.log('DASHBOARD_LOAD_MS', elapsed);
  });

  test('conversations page for prisma cert', async ({ page }) => {
    await activateProject(page, 'PRISMA-V3-CERTIFICATION');
    await page.goto('/conversations');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(500);
    const visible = await page.isVisible('text=Certificação PRISMA V3');
    console.log('CONVERSATIONS_PRISMA_VISIBLE', visible);
  });

  test('agents page', async ({ page }) => {
    await page.goto('/agents');
    await page.waitForLoadState('networkidle');
    const html = await page.content();
    console.log('AGENTS_HTML', html.substring(0, 4000));
  });

  test('channels page', async ({ page }) => {
    await page.goto('/channels');
    await page.waitForLoadState('networkidle');
    const html = await page.content();
    console.log('CHANNELS_HTML', html.substring(0, 4000));
  });

  test('notifications', async ({ page }) => {
    await page.goto('/notifications');
    await page.waitForLoadState('networkidle');
    const html = await page.content();
    console.log('NOTIFICATIONS_HTML', html.substring(0, 4000));
  });
});
