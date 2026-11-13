import { test, expect, deferInitialDaemonSnapshot } from './fixtures';
import type { Schema } from '../src/api/types';
import type { Request, WebSocketRoute } from '@playwright/test';

test('keeps private-chat presence live without loading the full profile', async ({ page, daemon }) => {
  const seeded = await page.request.post('/api/chat/private-messages', {
    data: { username: 'Presence Peer', text: 'Hello', messageId: crypto.randomUUID() },
  });
  expect(seeded.ok()).toBeTruthy();
  let profileRequests = 0;
  page.on('request', request => {
    if (/\/api\/users\/[^/]+\/profile$/.test(new URL(request.url()).pathname)) profileRequests++;
  });
  await page.goto('/chat/users/Presence%20Peer');
  const presence = page.locator('.chat-thread-identity [data-presence]');
  await expect(presence).toHaveAttribute('data-presence', 'online');
  await daemon.command('presence-away');
  await expect(presence).toHaveAttribute('data-presence', 'away');
  await expect(page.locator('.chat-messages')).toContainText('Hello');
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 900 });
    const topGap = await page.locator('.chat-messages').evaluate((messages) =>
      messages.firstElementChild!.getBoundingClientRect().top - messages.getBoundingClientRect().top);
    expect(topGap).toBeLessThan(50);
  }
  const composer = page.getByRole('textbox', { name: 'Message', exact: true });
  expect(await composer.evaluate((node) => node.getBoundingClientRect().height)).toBe(38);
  const longText = 'Long text should wrap visually without adding newline characters. '.repeat(12);
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await composer.fill(longText);
    await expect(composer).toHaveValue(longText);
    const dimensions = await composer.evaluate(node => ({
      height: node.getBoundingClientRect().height, width: node.clientWidth, contentWidth: node.scrollWidth,
    }));
    expect(dimensions.height).toBeGreaterThan(38);
    expect(dimensions.contentWidth).toBeLessThanOrEqual(dimensions.width);
    await composer.press('Enter');
    await expect(composer).toHaveValue(longText);
    await composer.clear();
    expect(await composer.evaluate(node => node.getBoundingClientRect().height)).toBe(38);
  }
  await page.goto('/chat/users/New%20Peer');
  await expect(presence).toHaveAttribute('data-presence', 'online');
  expect(profileRequests).toBe(0);
});

test('shows an unread room message while another chat is open and clears it on selection', async ({ page, daemon }) => {
  let roomListReads = 0;
  page.on('request', (request) => {
    if (request.method() === 'GET' && new URL(request.url()).pathname === '/api/chat/rooms') roomListReads++;
  });
  const seeded = await page.request.post('/api/chat/private-messages', {
    data: { username: 'Other Peer', text: 'Stay in this conversation', messageId: crypto.randomUUID() },
  });
  expect(seeded.ok()).toBeTruthy();
  const room = await page.request.post('/api/chat/rooms', { data: { roomName: 'room', remember: true } });
  expect(room.ok(), await room.text()).toBeTruthy();
  await page.goto('/chat');
  await expect(page.locator('.chat-thread-identity')).toContainText('Other Peer');
  await daemon.command('incoming-room-chat');
  const roomRow = page.locator('.chat-destination.room');
  await expect(roomRow.locator('.unread-count')).toHaveText('1');
  await expect(page.getByRole('navigation', { name: 'Primary navigation' })
    .getByRole('button', { name: /Chat/ }).locator('.nav-count')).toHaveText('1');
  await expect(page.locator('.chat-thread-identity')).toContainText('Other Peer');
  expect(roomListReads).toBeLessThanOrEqual(2); // Initial page plus one coalesced unread refresh.
  await roomRow.click();
  await expect(page.locator('.chat-messages')).toContainText('anyone heard the new Broadcast archival release?');
  await expect(roomRow.locator('.unread-count')).toHaveCount(0);
  const message = page.locator('.room-message').filter({ hasText: 'anyone heard the new Broadcast archival release?' });
  await expect(message).toBeVisible();
  await expect(message.locator('.room-message-meta')).toContainText('neonrain');
  await expect(message.locator('.message-body')).toHaveText('anyone heard the new Broadcast archival release?');
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 900 });
    const bounds = await message.boundingBox();
    const pane = page.locator('.chat-messages');
    const paneBounds = await pane.boundingBox();
    const paddingLeft = await pane.evaluate(element => Number.parseFloat(getComputedStyle(element).paddingLeft));
    expect(bounds).not.toBeNull();
    expect(paneBounds).not.toBeNull();
    expect(bounds!.width).toBeGreaterThan(paneBounds!.width * 0.75);
    expect(bounds!.x).toBeCloseTo(paneBounds!.x + paddingLeft, 0);
    expect(bounds!.x).toBe(width === 1440 ? 496 : 174);
  }
});

test('recovers a silently missed chat batch without a connection transition', async ({ page }) => {
  const seed = await page.request.post('/api/chat/private-messages', {
    data: { username: 'Gap Peer', text: 'Before gap', messageId: crypto.randomUUID() },
  });
  expect(seed.ok()).toBeTruthy();
  const conversationId = ((await seed.json()) as Schema<'ChatMessageDto'>).targetId;
  let snapshotReads = 0;
  let subscribed!: () => void;
  const ready = new Promise<void>(resolve => { subscribed = resolve; });
  page.on('response', response => {
    if (new URL(response.url()).pathname === `/api/chat/conversations/${conversationId}/snapshot`
      && response.ok() && ++snapshotReads >= 2) subscribed();
  });
  let dropped!: () => void;
  const missed = new Promise<void>(resolve => { dropped = resolve; });
  let didDrop = false;
  await page.routeWebSocket('**/api/events?**', route => {
    const server = route.connectToServer();
    server.onMessage(message => {
      if (!didDrop && String(message).includes('Missed while connected')) {
        didDrop = true;
        dropped();
        return;
      }
      route.send(message);
    });
  });
  await page.goto('/chat/users/Gap%20Peer');
  await ready;
  const messages = page.locator('.chat-messages');
  await expect(messages).toContainText('Before gap');
  const status = page.locator('.connection-status').locator('strong').last();
  await expect(status).toContainText('Connected');
  const send = async (text: string) => {
    const response = await page.request.post('/api/chat/private-messages', {
      data: { username: 'Gap Peer', text, messageId: crypto.randomUUID() },
    });
    expect(response.ok()).toBeTruthy();
  };
  await send('Missed while connected');
  await missed;
  await send('After the gap');
  await expect(messages.getByText('Missed while connected', { exact: true })).toBeAttached();
  await expect(messages.getByText('After the gap', { exact: true })).toBeAttached();
  await expect(messages.locator('[data-message-id]')).toHaveCount(3);
  await expect(status).toContainText('Connected');
  expect(snapshotReads).toBeGreaterThan(2);
});

test('preserves reading position and catches up across a multi-page connection outage', async ({ page, daemon }) => {
  await page.clock.install();
  let socket: WebSocketRoute | undefined;
  let outage = false;
  let restoreConnection!: () => void;
  const connectionRestored = new Promise<void>(resolve => { restoreConnection = resolve; });
  await page.route('**/api/events/negotiate?**', async route => {
    if (outage) await connectionRestored;
    await route.continue();
  });
  await page.routeWebSocket('**/api/events?**', route => {
    if (outage) void route.close({ code: 1012, reason: 'E2E network unavailable' });
    else { socket = route; route.connectToServer(); }
  });
  const send = async (text: string) => {
    const response = await page.request.post('/api/chat/private-messages', {
      data: { username: 'Reconnect Peer', text, messageId: crypto.randomUUID() },
    });
    expect(response.ok()).toBeTruthy();
  };
  await send(Array.from({ length: 300 }, (_, i) => `Reading line ${i}`).join(' '));
  await page.goto('/chat/users/Reconnect%20Peer');
  const status = page.locator('.connection-status').locator('strong').last();
  await expect(status).toContainText('Connected');
  const presence = page.locator('.chat-thread-identity [data-presence]');
  await expect(presence).toHaveAttribute('data-presence', 'online');
  const messages = page.locator('.chat-messages');
  await expect(messages).toContainText('Reading line 59');
  await messages.evaluate(async element => {
    await new Promise(requestAnimationFrame);
    element.scrollTop = 0;
  });
  outage = true;
  try {
    await socket!.close({ code: 1012, reason: 'E2E interruption' });
    await expect(status).toHaveText('Daemon reconnecting');
    await expect(presence).toHaveAttribute('data-presence', 'disconnected');
    // Cross one history page, not a load test. Recovery must not silently skip
    // the first messages received while the browser's live transport was down.
    await daemon.command('seed-chat-outage');
  } finally {
    outage = false;
    restoreConnection();
  }
  await page.clock.fastForward(2_000); // Resume SignalR without waiting for its retry delay.
  await expect(status).toContainText('Connected');
  await expect(presence).toHaveAttribute('data-presence', 'online');
  await expect(messages.getByText('Offline 502', { exact: true })).toBeAttached();
  await expect(messages.getByText('Offline 000', { exact: true })).toBeAttached();
  await expect(messages.locator('[data-message-id]')).toHaveCount(504);
  await messages.evaluate(() => new Promise(requestAnimationFrame));
  expect(await messages.evaluate(element => element.scrollTop)).toBeLessThanOrEqual(2);
  await expect(messages).toContainText('Reading line 59');
});

test('recovers failed conversation loading without losing a composed draft', async ({ page }) => {
  const seeded = await page.request.post('/api/chat/private-messages', { data: { username: 'Peer', text: 'Retained message', messageId: crypto.randomUUID() } });
  expect(seeded.ok()).toBeTruthy();
  const message = await seeded.json() as Schema<'ChatMessageDto'>;
  const route = `**/api/chat/conversations/${message.targetId}/messages?**`;
  const releaseSnapshot = await deferInitialDaemonSnapshot(page);
  try {
    await page.route(route, (request) => request.abort());
    await page.goto('/chat');
    await expect(page.getByRole('alert')).toContainText('Chat unavailable');
    await expect(page.locator('.chat-messages')).toContainText('Messages unavailable');
    await expect(page.locator('.chat-messages')).not.toContainText('No messages yet');
    const composer = page.getByRole('textbox', { name: 'Message', exact: true });
    releaseSnapshot();
    await expect(composer).toBeEnabled();
    await expect(page.getByRole('alert')).toContainText('Chat unavailable');
    await composer.fill('Draft survives retry');
    await page.unroute(route);
    await page.getByRole('button', { name: 'Retry', exact: true }).click();
    await expect(page.locator('.chat-messages')).toContainText('Retained message');
    await expect(composer).toHaveValue('Draft survives retry');
    await expect(page.getByRole('alert')).toHaveCount(0);
  } finally { releaseSnapshot(); }
});

test('does not pull a reader to the bottom when a peer message arrives', async ({ page, daemon }) => {
  await page.clock.install();
  await page.goto('/chat/users/Scroll%20Peer');
  const composer = page.getByRole('textbox', { name: 'Message', exact: true });
  await composer.fill(Array.from({ length: 300 }, (_, index) => `History line ${index}`).join(' '));
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  const messages = page.locator('.chat-messages');
  await expect(messages).toContainText('History line 59');
  await messages.evaluate(async element => {
    await new Promise(requestAnimationFrame);
    element.scrollTop = element.scrollHeight;
  });
  expect(await messages.evaluate(element => element.scrollHeight > element.clientHeight)).toBe(true);
  await page.clock.pauseAt(await page.evaluate(() => Date.now() + 1_000));
  await daemon.command('incoming-chat');
  await expect(messages).toContainText('Incoming while reading');
  // Move away while the incoming message's follow-to-bottom frame is pending.
  await messages.evaluate(element => { element.scrollTop = 0; });
  await page.clock.runFor(16);
  expect(await messages.evaluate(element => element.scrollTop)).toBeLessThanOrEqual(2);
  await messages.evaluate(element => { element.scrollTop = element.scrollHeight; });
  await daemon.command('incoming-chat');
  await expect(messages).toContainText('Incoming while reading 502');
  await page.clock.runFor(16);
  expect(await messages.evaluate(element => element.scrollHeight - element.clientHeight - element.scrollTop)).toBeLessThanOrEqual(2);
});

test('keeps the first visible message anchored when loading an older page', async ({ page, daemon }) => {
  // Cross the real 500-message history page in one retained domain batch.
  // Separate send/live journeys cover transport; this case tests anchoring.
  await daemon.command('seed-chat-history');
  await page.goto('/chat/users/History%20Peer');
  const messages = page.locator('.chat-messages');
  await expect(messages).toContainText('History 502');
  // History renders before initial live-subscription setup finishes. Wait for
  // the paging action to be ready, then drain its queued initial scroll before
  // choosing the reading position; visibility alone does not mean on-screen.
  const loadMore = page.getByRole('button', { name: 'Load more', exact: true });
  await expect(page.getByLabel('Connection status')).toContainText('Connected');
  await expect(loadMore).toBeEnabled();
  await messages.evaluate(async element => {
    await new Promise(requestAnimationFrame);
    element.scrollTop = 0;
  });
  const anchor = messages.getByText('History 003', { exact: true });
  await expect(anchor).toBeInViewport();
  const before = await anchor.boundingBox();
  await loadMore.click();
  await expect(messages.getByText('History 000', { exact: true })).toBeAttached();
  await messages.evaluate(() => new Promise(requestAnimationFrame));
  await expect(anchor).toBeInViewport();
  const after = await anchor.boundingBox();
  expect(before).not.toBeNull();
  expect(after).not.toBeNull();
  expect(Math.abs(after!.y - before!.y)).toBeLessThanOrEqual(1);
});

test('opens an exact peer conversation outside the loaded sidebar page', async ({ page }) => {
  for (const username of ['Peer', 'peer']) {
    const response = await page.request.post('/api/chat/private-messages', {
      data: { username, text: `Retained for ${username}`, messageId: crypto.randomUUID() },
    });
    expect(response.ok()).toBeTruthy();
  }
  const releaseSnapshot = await deferInitialDaemonSnapshot(page);
  let releaseList!: () => void;
  const listReleased = new Promise<void>(resolve => { releaseList = resolve; });
  let firstPage = true;
  // Hold a real one-item page until the exact-peer lookup has opened its
  // out-of-page target. A late list must not erase that fetched conversation.
  await page.route('**/api/chat/conversations?*', async route => {
    const url = new URL(route.request().url());
    url.searchParams.set('limit', '1');
    if (!firstPage) { await route.continue({ url: url.toString() }); return; }
    firstPage = false;
    const response = await route.fetch({ url: url.toString() });
    await listReleased;
    await route.fulfill({ response });
  });
  try {
    await page.goto('/chat/users/Peer');
    await expect(page.locator('.chat-messages')).toContainText('Retained for Peer');
    await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveAttribute('placeholder', 'Message Peer…');
    const selected = page.locator('.chat-destination.user.active');
    await expect(selected.locator('strong')).toHaveText('Peer');
    releaseList();
    await expect(page.getByRole('button', { name: 'Open conversation with peer', exact: true })).toBeVisible();
    await expect(selected.locator('strong')).toHaveText('Peer');
    const continuedReads: Request[] = [];
    const recordContinuation = (request: Request) => {
      const path = new URL(request.url()).pathname;
      if (request.method() === 'GET' && ['/api/chat/conversations', '/api/chat/rooms'].includes(path)) continuedReads.push(request);
    };
    page.on('request', recordContinuation);
    await page.getByRole('button', { name: 'Load more', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Load more', exact: true })).toHaveCount(0);
    await expect(page.locator('.chat-destination.user')).toHaveCount(2);
    await expect(page.locator('.chat-messages')).toContainText('Retained for Peer');
    page.off('request', recordContinuation);
    expect(continuedReads.map(request => new URL(request.url()).pathname)).toEqual(['/api/chat/conversations']);
    for (const request of continuedReads) await (await request.response())?.finished();
    await page.getByRole('button', { name: 'Chat options', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Delete chat', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Open conversation with Peer', exact: true })).toHaveCount(0);
    await expect(page.locator('.chat-thread-heading')).toContainText('peer');
    await expect(page.locator('.chat-messages')).toContainText('Retained for peer');
    releaseSnapshot();
    await page.goto('/chat');
    await expect(page.getByRole('button', { name: 'Open conversation with Peer', exact: true })).toHaveCount(0);
    await expect(page.locator('.chat-messages')).toContainText('Retained for peer');
  } finally { releaseList(); releaseSnapshot(); }
});

test('ignores Enter, pastes line breaks as spaces and sends with Ctrl+Enter', async ({ page }) => {
  const username = ' Peer ';
  const text = '  before first second after  ';
  let sends = 0;
  page.on('request', request => {
    if (new URL(request.url()).pathname === '/api/chat/private-messages' && request.method() === 'POST') sends++;
  });
  await page.goto('/chat');
  await page.getByRole('button', { name: 'Message user', exact: true }).click();
  await page.getByRole('textbox', { name: 'Username', exact: true }).fill(username);
  await page.getByRole('button', { name: 'Open', exact: true }).click();
  const composer = page.getByRole('textbox', { name: 'Message', exact: true });
  await expect(composer).toHaveAttribute('wrap', 'soft');
  await composer.fill(text);
  await composer.press('Enter');
  await expect(composer).toHaveValue(text);
  await composer.press('Shift+Enter');
  await expect(composer).toHaveValue(text);
  for (const newline of ['\n', '\r', '\r\n', '\v', '\f', '\u0085', '\u2028', '\u2029']) {
    await composer.fill('  before replace after  ');
    await composer.evaluate((node, newline) => {
      const input = node as HTMLTextAreaElement;
      input.setSelectionRange(9, 16);
      const clipboardData = new DataTransfer();
      clipboardData.setData('text/plain', `first${newline}second`);
      input.dispatchEvent(new ClipboardEvent('paste', { clipboardData, bubbles: true, cancelable: true }));
    }, newline);
    await expect(composer).toHaveValue(text);
    expect(await composer.evaluate(node => (node as HTMLTextAreaElement).selectionStart)).toBe(21);
  }
  expect(sends).toBe(0);
  const sent = page.waitForResponse((response) => new URL(response.url()).pathname === '/api/chat/private-messages' && response.request().method() === 'POST');
  await composer.press('Control+Enter');
  const response = await sent;
  expect(response.ok()).toBeTruthy();
  expect(response.request().postDataJSON()).toMatchObject({ username, text });
  const message = await response.json() as Schema<'ChatMessageDto'>;
  expect(message.text).toBe(text);
  expect(sends).toBe(1);
  await page.reload();
  await expect(page.locator('.chat-messages')).toContainText('before first second after');
  const retained = await page.request.get(`/api/chat/conversations/${message.targetId}/messages`);
  expect(retained.ok()).toBeTruthy();
  expect((await retained.json()).items).toEqual(expect.arrayContaining([expect.objectContaining({ text })]));
});

test('keeps a delayed send response in its original conversation', async ({ page }) => {
  const conversations: string[] = [];
  for (const username of ['FirstPeer', 'SecondPeer']) {
    const response = await page.request.post('/api/chat/private-messages', { data: { username, text: `Hello ${username}`, messageId: crypto.randomUUID() } });
    expect(response.ok()).toBeTruthy();
    conversations.push((await response.json() as Schema<'ChatMessageDto'>).targetId);
  }
  let release!: () => void;
  let received!: () => void;
  const held = new Promise<void>((resolve) => release = resolve);
  const requestReceived = new Promise<void>((resolve) => received = resolve);
  const path = `/api/chat/conversations/${conversations[0]}/messages`;
  await page.route(`**${path}`, async (route) => {
    if (route.request().method() !== 'POST') return route.continue();
    const response = await route.fetch();
    received();
    await held;
    await route.fulfill({ response });
  });
  try {
    await page.goto('/chat');
    await page.getByRole('button', { name: 'Open conversation with FirstPeer', exact: true }).click();
    const composer = page.getByRole('textbox', { name: 'Message', exact: true });
    await composer.fill('Delayed first response');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await requestReceived;
    await page.getByRole('button', { name: 'Open conversation with SecondPeer', exact: true }).click();
    await expect(page.locator('.chat-messages')).toContainText('Hello SecondPeer');
    await composer.fill('Unsent second draft');
    const returned = page.waitForResponse((response) => new URL(response.url()).pathname === path && response.request().method() === 'POST');
    release();
    await (await returned).finished();
    // Let the browser commit the response-driven render before inspecting it.
    await page.evaluate(() => new Promise<void>((resolve) => requestAnimationFrame(() => resolve())));
    await expect(composer).toHaveValue('Unsent second draft');
    await expect(page.locator('.chat-messages')).not.toContainText('Delayed first response');
    await page.getByRole('button', { name: 'Open conversation with FirstPeer', exact: true }).click();
    await expect(page.locator('.chat-messages')).toContainText('Delayed first response');
  } finally { release(); }
});
