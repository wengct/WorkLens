// Release build + Playwright required. Uses isolated runtime data.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const net = require('node:net');
(async () => {
    const root = path.resolve(__dirname, '../..');
    const temporary = await fs.mkdtemp(path.join(os.tmpdir(), 'worklens-layout-'));
    const socket = net.createServer();
    await new Promise(r => socket.listen(0, '127.0.0.1', r));
    const url = `http://127.0.0.1:${socket.address().port}`;
    await new Promise(r => socket.close(r));
    const server = spawn('dotnet', [path.join(root, 'bin/Release/net10.0/WorkLens.dll')], {
        cwd: root, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
        env: { ...process.env, ASPNETCORE_URLS: url,
            ConnectionStrings__WorkLens: `Data Source=${path.join(temporary, 'data/worklens.db')}`,
            WorkLens__BackupPath: path.join(temporary, 'backups'), WorkLens__LogPath: path.join(temporary, 'logs') }
    });
    let output = '', browser;
    server.stdout.on('data', c => output += c);
    server.stderr.on('data', c => output += c);
    try {
        for (let i = 0; i < 100; i++) {
            if (await fetch(`${url}/healthz`).then(r => r.ok).catch(() => false)) break;
            if (i === 99) throw new Error(output);
            await new Promise(r => setTimeout(r, 200));
        }
        browser = await chromium.launch({ headless: true, channel: process.env.WORKLENS_TEST_BROWSER || 'msedge' });
        const page = await browser.newPage();
        for (const width of [1008, 1440, 1326, 1325, 1200, 901, 900, 651, 650, 320]) {
            await page.setViewportSize({ width, height: 1000 });
            await page.goto(url);
            await page.locator('.draft-editor fieldset:not([disabled])').waitFor();
            await page.locator('.today-quick-add .CodeMirror').waitFor();
            const guide = page.locator('.getting-started-dialog');
            if (await guide.isVisible()) await guide.getByRole('button', { name: '略過', exact: true }).click();
            const toggle = page.locator('.mobile-menu-toggle');
            const navigation = page.getByRole('navigation', { name: '主要導覽' });
            if (width <= 650) {
                assert.equal(await toggle.getAttribute('aria-expanded'), 'false');
                assert.equal(await navigation.isVisible(), false);
                assert.ok((await page.locator('.sidebar').boundingBox()).height <= 80, 'Mobile header stays compact');
                await toggle.click();
                await navigation.waitFor({ state: 'visible' });
                assert.equal(await navigation.isVisible(), true);
                assert.equal(await toggle.getAttribute('aria-expanded'), 'true');
                await navigation.getByRole('link').first().focus();
                await page.keyboard.press('Escape');
                await navigation.waitFor({ state: 'hidden' });
                await page.waitForFunction(() => document.activeElement?.classList.contains('mobile-menu-toggle'));
                assert.equal(await navigation.isVisible(), false);
                assert.equal(await toggle.evaluate(el => el === document.activeElement), true);
                await page.keyboard.press('Enter');
                await navigation.getByRole('link', { name: '專案', exact: false }).click();
                await page.waitForURL('**/projects');
                assert.equal(await navigation.isVisible(), false);
                await toggle.click();
                await navigation.waitFor({ state: 'visible' });
                await page.setViewportSize({ width: 1440, height: 600 });
                assert.equal(await navigation.isVisible(), true);
                assert.equal(await toggle.isVisible(), false);
                await page.setViewportSize({ width, height: 1000 });
                await navigation.getByRole('link', { name: '工作填寫', exact: false }).click();
                await page.waitForURL(url + '/');
                await page.locator('.today-quick-add .CodeMirror').waitFor();
                assert.equal(await navigation.isVisible(), false);
            } else {
                assert.equal(await navigation.isVisible(), true);
                assert.equal(await toggle.isVisible(), false);
            }
            if (process.env.WORKLENS_LAYOUT_BASELINE) {
                await page.addStyleTag({ content: '.today-workspace { grid-template-columns: minmax(0, 1.18fr) minmax(340px, .82fr); } .today-quick-add { container-type: normal; }' });
            }
            const failures = await page.evaluate(() => {
                const panel = document.querySelector('.today-quick-add').getBoundingClientRect();
                return [...document.querySelectorAll('.today-quick-add input, .today-quick-add select, .today-quick-add .CodeMirror, .today-quick-add .editor-toolbar')]
                    .filter(el => { const r = el.getBoundingClientRect(); return r.right > panel.right + 1 || r.left < panel.left - 1; })
                    .map(el => el.className || el.tagName);
            });
            assert.deepEqual(failures, [], `Form overflows at ${width}px`);
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, `Page overflows at ${width}px`);
            for (const open of [true, false]) {
                await page.locator('details.evidence-panel').evaluateAll((elements, value) => elements.forEach(el => el.open = value), open);
                assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, `Expanded content overflows at ${width}px`);
            }
            await page.screenshot({ path: path.join(temporary, `layout-${width}.png`), fullPage: true });
            console.log(`PASS ${width}px`);
        }
        console.log(`Screenshots: ${temporary}`);
    } finally {
        await browser?.close();
        server.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
