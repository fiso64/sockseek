import { test, expect, deferInitialDaemonSnapshot } from './fixtures';
import type { Schema } from '../src/api/types';
import type { Request, WebSocketRoute } from '@playwright/test';

test('shows observed private-chat presence without loading the full profile', async ({ page }) => {
  const seeded = await page.request.post('/api/chat/private-messages', {
    data: { username: 'Presence Peer', text: 'Hello', messageId: crypto.randomUUID() },
  });
  expect(seeded.ok()).toBeTruthy();
  let profileRequests = 0;
  page.on('request', request => {
    if (/\/api\/users\/[^/]+\/profile$/.test(new URL(request.url()).pathname)) profileRequests++;
  });
  const observed = page.waitForResponse(response =>
    /\/api\/users\/Presence%20Peer\/presence$/.test(new URL(response.url()).pathname));
  await page.goto('/chat/users/Presence%20Peer');
  expect((await observed).ok()).toBeTruthy();
  await expect(page.locator('.chat-thread-identity small')).toContainText(/online · observed (just now|\d+ min ago)/);
  await expect(page.locator('.chat-messages')).toContainText('Hello');
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 900 });
    const topGap = await page.locator('.chat-messages').evaluate((messages) =>
      messages.firstElementChild!.getBoundingClientRect().top - messages.getBoundingClientRect().top);
    expect(topGap).toBeLessThan(50);
  }
  const composer = page.getByRole('textbox', { name: 'Message', exact: true });
  expect(await composer.evaluate((node) => node.getBoundingClientRect().height)).toBe(38);
  await composer.fill('First line\nSecond line');
  expect(await composer.evaluate((node) => node.getBoundingClientRect().height)).toBeGreaterThan(38);
  await composer.clear();
  expect(await composer.evaluate((node) => node.getBoundingClientRect().height)).toBe(38);
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
  await expect(status).toHaveText('Connected');
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
  await expect(status).toHaveText('Connected');
  expect(snapshotReads).toBeGreaterThan(2);
});

test('preserves reading position and catches up across a multi-page connection outage', async ({ page }) => {
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
  await send(Array.from({ length: 60 }, (_, i) => `Reading line ${i}`).join('\n'));
  await page.goto('/chat/users/Reconnect%20Peer');
  const status = page.locator('.connection-status').locator('strong').last();
  await expect(status).toHaveText('Connected');
  const messages = page.locator('.chat-messages');
  await expect(messages).toContainText('Reading line 59');
  await messages.evaluate(async element => {
    await new Promise(requestAnimationFrame);
    element.scrollTop = 0;
  });
  outage = true;
  try {
    await socket!.close({ code: 1012, reason: 'E2E interruption' });
    await expect(status).toContainText('Reconnecting');
    // Cross one history page, not a load test. Recovery must not silently skip
    // the first messages received while the browser's live transport was down.
    for (let i = 0; i < 103; i++) await send(`Offline ${String(i).padStart(3, '0')}`);
  } finally {
    outage = false;
    restoreConnection();
  }
  await expect(status).toHaveText('Connected');
  await expect(messages.getByText('Offline 102', { exact: true })).toBeAttached();
  await expect(messages.getByText('Offline 000', { exact: true })).toBeAttached();
  await expect(messages.locator('[data-message-id]')).toHaveCount(104);
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
  await page.goto('/chat/users/Scroll%20Peer');
  const composer = page.getByRole('textbox', { name: 'Message', exact: true });
  await composer.fill(Array.from({ length: 60 }, (_, index) => `History line ${index}`).join('\n'));
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  const messages = page.locator('.chat-messages');
  await expect(messages).toContainText('History line 59');
  await messages.evaluate(async element => {
    await new Promise(requestAnimationFrame);
    element.scrollTop = 0;
  });
  expect(await messages.evaluate(element => element.scrollHeight > element.clientHeight)).toBe(true);
  await daemon.command('incoming-chat');
  await expect(messages).toContainText('Incoming while reading');
  await messages.evaluate(() => new Promise(requestAnimationFrame));
  expect(await messages.evaluate(element => element.scrollTop)).toBeLessThanOrEqual(2);
  await messages.evaluate(element => { element.scrollTop = element.scrollHeight; });
  await daemon.command('incoming-chat');
  await expect(messages).toContainText('Incoming while reading 502');
  await messages.evaluate(() => new Promise(requestAnimationFrame));
  expect(await messages.evaluate(element => element.scrollHeight - element.clientHeight - element.scrollTop)).toBeLessThanOrEqual(2);
});

test('keeps the first visible message anchored when loading an older page', async ({ page, daemon }) => {
  // Cross the real 500-message history page in one retained domain batch.
  // Separate send/live journeys cover transport; this case tests anchoring.
  await daemon.command('seed-chat-history');
  await page.goto('/chat/users/History%20Peer');
  const messages = page.locator('.chat-messages');
  await expect(messages).toContainText('History 502');
  await messages.evaluate(async element => {
    await new Promise(requestAnimationFrame);
    element.scrollTop = 0;
  });
  const anchor = messages.getByText('History 003', { exact: true });
  await expect(anchor).toBeVisible();
  const before = await anchor.boundingBox();
  await page.getByRole('button', { name: 'Load earlier messages', exact: true }).click();
  await expect(messages.getByText('History 000', { exact: true })).toBeAttached();
  await messages.evaluate(() => new Promise(requestAnimationFrame));
  const after = await anchor.boundingBox();
  expect(before).not.toBeNull();
  expect(after).not.toBeNull();
  expect(Math.abs(after!.y - before!.y)).toBeLessThanOrEqual(1);
});

test('opens an exact peer conversation outside the loaded sidebar page', async ({ page }) => {
  for (const username of ['Older Peer', 'Newer Peer']) {
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
    await page.goto('/chat/users/Older%20Peer');
    await expect(page.locator('.chat-messages')).toContainText('Retained for Older Peer');
    await expect(page.getByRole('textbox', { name: 'Message', exact: true })).toHaveAttribute('placeholder', 'Message Older Peer…');
    const selected = page.locator('.chat-destination.user.active');
    await expect(selected.locator('strong')).toHaveText('Older Peer');
    releaseList();
    await expect(page.getByRole('button', { name: 'Open conversation with Newer Peer', exact: true })).toBeVisible();
    await expect(selected.locator('strong')).toHaveText('Older Peer');
    const continuedReads: Request[] = [];
    const recordContinuation = (request: Request) => {
      const path = new URL(request.url()).pathname;
      if (request.method() === 'GET' && ['/api/chat/conversations', '/api/chat/rooms'].includes(path)) continuedReads.push(request);
    };
    page.on('request', recordContinuation);
    await page.getByRole('button', { name: 'Load more chats', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Load more chats', exact: true })).toHaveCount(0);
    await expect(page.locator('.chat-destination.user')).toHaveCount(2);
    await expect(page.locator('.chat-messages')).toContainText('Retained for Older Peer');
    page.off('request', recordContinuation);
    expect(continuedReads.map(request => new URL(request.url()).pathname)).toEqual(['/api/chat/conversations']);
    for (const request of continuedReads) await (await request.response())?.finished();
    await page.getByRole('button', { name: 'Chat options', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Delete chat', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Open conversation with Older Peer', exact: true })).toHaveCount(0);
    await expect(page.locator('.chat-thread-heading')).toContainText('Newer Peer');
    await expect(page.locator('.chat-messages')).toContainText('Retained for Newer Peer');
    releaseSnapshot();
    await page.goto('/chat');
    await expect(page.getByRole('button', { name: 'Open conversation with Older Peer', exact: true })).toHaveCount(0);
    await expect(page.locator('.chat-messages')).toContainText('Retained for Newer Peer');
  } finally { releaseList(); releaseSnapshot(); }
});

test('preserves the exact recipient and keyboard-composed multiline message when starting a conversation', async ({ page }) => {
  const username = ' Peer ';
  const text = '  Indented message\nwith trailing space  \n';
  await page.goto('/chat');
  await page.getByRole('button', { name: 'Message user', exact: true }).click();
  await page.getByRole('textbox', { name: 'Username', exact: true }).fill(username);
  await page.getByRole('button', { name: 'Open', exact: true }).click();
  const composer = page.getByRole('textbox', { name: 'Message', exact: true });
  await composer.fill('  Indented message\nwith trailing space  ');
  await composer.press('Enter');
  await expect(composer).toHaveValue(text);
  const sent = page.waitForResponse((response) => new URL(response.url()).pathname === '/api/chat/private-messages' && response.request().method() === 'POST');
  await composer.press('Control+Enter');
  const response = await sent;
  expect(response.ok()).toBeTruthy();
  expect(response.request().postDataJSON()).toMatchObject({ username, text });
  const message = await response.json() as Schema<'ChatMessageDto'>;
  expect(message.text).toBe(text);
  await page.reload();
  await expect(page.locator('.chat-messages')).toContainText('Indented message');
  const retained = await page.request.get(`/api/chat/conversations/${message.targetId}/messages`);
  expect(retained.ok()).toBeTruthy();
  expect((await retained.json()).items).toEqual(expect.arrayContaining([expect.objectContaining({ text })]));
});

test('does not merge case-distinct usernames when opening a conversation', async ({ page }) => {
  for (const username of ['Peer', 'peer']) {
    const response = await page.request.post('/api/chat/private-messages', { data: { username, text: `Only ${username}`, messageId: crypto.randomUUID() } });
    expect(response.ok()).toBeTruthy();
  }
  await page.goto('/chat');
  await expect(page.getByRole('button', { name: 'Open conversation with Peer', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Open conversation with peer', exact: true })).toBeVisible();
  for (const username of ['Peer', 'peer']) {
    await page.getByRole('button', { name: 'Message user', exact: true }).click();
    await page.getByRole('textbox', { name: 'Username', exact: true }).fill(username);
    await page.getByRole('button', { name: 'Open', exact: true }).click();
    await expect(page.locator('.chat-messages')).toContainText(`Only ${username}`);
    await expect(page.locator('.chat-messages')).not.toContainText(`Only ${username === 'Peer' ? 'peer' : 'Peer'}`);
  }
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
