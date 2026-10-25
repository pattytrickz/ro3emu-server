#!/usr/bin/env node
// Class skill status: one list per class line, built from the exported game data, the server code, the tests and
// (optionally) the bug reports in a Discord forum. It states what is known about each skill, not that it is bug-free.
//
//   node tools/ClassStatus/class-status.mjs                 preview on stdout (needs the exported data/ folder)
//   node tools/ClassStatus/class-status.mjs --out list.md   preview into a file
//   node tools/ClassStatus/class-status.mjs --post          edit the lists in the Discord status channel
//
// Without DISCORD_BOT_TOKEN no reports are read, so no skill shows as "open" or "fixed". Environment:
//   DISCORD_BOT_TOKEN       the bot's token (never write it into a file)
//   DISCORD_GUILD           server id, for report links
//   DISCORD_BUG_FORUM       forum channel id; its tags name the status (Investigating / Need more info / Fix coming /
//                           Fixed / Not a bug) and the class line ("Swordsman line" ...)
//   DISCORD_STATUS_CHANNEL  text channel that holds the lists (only the bot may post there); needed for --post
// When running behind a proxy, set NODE_USE_ENV_PROXY=1.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const CONFIG = path.join(REPO, 'data', 'config');
const LINES = { 1: 'Swordsman', 2: 'Mage', 3: 'Archer', 4: 'Acolyte', 5: 'Thief', 6: 'Merchant' };
// Reports that name no skill but belong to several (the three bolt spells share one cast animation).
const ALIAS = {
    ...Object.fromEntries(['fire bolt', 'cold bolt', 'lightning bolt'].map(n => [n, ['mage bolt', 'mage bolts', 'bolt spell', 'bolt spells']])),
    'jupitel thunder': ['jupital thunder', 'jupiter thunder'],
};

const ICON = { open: '🔴', nothing: '⚪', fixed: '✅', tested: '🧪', logic: '🟢', data: '🟡' };
const LEGEND = [
    '🧪 Tested: an automated test covers it',
    '🟢 Server logic: the server has code for it, no test yet',
    '🟡 Data only: damage, buffs and stats come from the game data and should work; extra effects are unchecked',
    '✅ Fixed after a report',
    '🔴 Open report (linked)',
    '⚪ Not found: the server has no code or data for it, so it probably does nothing',
].join('\n');
const INTRO = date => `Made automatically from the game data, the server code, our tests and your bug reports (${date}). ` +
    'It shows what we know, not that a skill is perfect: tested still means "a test covers it". ' +
    'If a skill looks wrong, post in #bug-reports with your class tag.';

// ---- game data ---------------------------------------------------------------------------------------------------
function table(name) {
    const raw = JSON.parse(fs.readFileSync(path.join(CONFIG, name), 'utf8'));
    return Array.isArray(raw) ? raw : Object.values(raw);
}

function loadData() {
    const text = new Map(table('Localization_en.json').map(r => [r._iID, r._kDes]));
    const name = v => (Array.isArray(v) && v.length ? text.get(v[0]) ?? '' : '');
    const skillRows = new Map();
    for (const r of table('SkillConfig.json')) {
        if (!skillRows.has(r._iSkillID)) skillRows.set(r._iSkillID, []);
        skillRows.get(r._iSkillID).push(r);
    }
    for (const rows of skillRows.values()) rows.sort((a, b) => a._iLevel - b._iLevel);
    const jobName = new Map(table('JobConfig.json').map(r => [r._iID, name(r._iName)]));
    const tree = new Map();
    for (const r of table('SkillTreeConfig.json'))
        for (const k of ['_iX1', '_iX2', '_iX3', '_iX4', '_iX5', '_iX6', '_iX7'])
            if (r[k]) (tree.get(r._iJob) ?? tree.set(r._iJob, new Set()).get(r._iJob)).add(r[k]);
    return { name, skillRows, jobName, tree };
}

// ---- what the server code and the tests mention -------------------------------------------------------------------
function* sourceFiles(dir, skip = []) {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
        const p = path.join(dir, e.name);
        if (e.isDirectory()) {
            if (e.name === 'bin' || e.name === 'obj' || skip.some(s => p.endsWith(s))) continue;
            yield* sourceFiles(p, skip);
        } else if (e.name.endsWith('.cs')) yield p;
    }
}

function corpus(dir, skip) {
    const nums = new Map(), strs = new Map();
    const add = (m, k, f) => (m.get(k) ?? m.set(k, new Set()).get(k)).add(f);
    for (const file of sourceFiles(dir, skip)) {
        const src = fs.readFileSync(file, 'utf8'), base = path.basename(file);
        for (const m of src.matchAll(/(?<![\w.])(\d{5,8})(?![\w.])/g)) add(nums, Number(m[1]), base);
        for (const m of src.matchAll(/"([A-Za-z_][A-Za-z0-9_]{2,40})"/g)) add(strs, m[1], base);
    }
    return { nums, strs };
}

const skillOf = n => (n >= 1_000_000 ? Math.floor(n / 100) : n);

function refs(sid, scripts, { nums, strs }) {
    const hit = new Set();
    for (const [n, files] of nums) if (skillOf(n) === sid) files.forEach(f => hit.add(f));
    for (const s of scripts) if (s && strs.has(s)) strs.get(s).forEach(f => hit.add(f));
    return [...hit];
}

const truthy = v => (Array.isArray(v) ? v.length > 0 : v && typeof v === 'object' ? Object.keys(v).length > 0 : Boolean(v));
const positive = v => Array.isArray(v) && v.some(x => typeof x === 'number' && x > 0);

function analyse(sid, data, src, tests) {
    const rows = data.skillRows.get(sid), r0 = rows[0];
    const scripts = new Set(rows.map(r => r._kScriptLogic).filter(Boolean));
    // a passive's behaviour often lives in the row its _iSpecialEffect points at
    const special = [...new Set(rows.flatMap(r => [r._iSpecialEffect].flat().filter(e => typeof e === 'number' && e > 0)))];
    for (const e of special)
        for (const rr of data.skillRows.get(e) ?? data.skillRows.get(Math.floor(e / 100)) ?? [])
            if (rr._kScriptLogic) scripts.add(rr._kScriptLogic);
    const srcRefs = refs(sid, scripts, src);
    return {
        name: data.name(r0._iName), active: r0._iRelease !== 0,
        dmg: rows.some(r => positive(r._kDamageParam1) || positive(r._kDamageParam2)),
        buff: rows.some(r => truthy(r._kBuff1) || truthy(r._kBuff2)), attri: rows.some(r => truthy(r._kAttri)),
        src: srcRefs.length ? srcRefs : special.flatMap(e => [...(src.nums.get(e) ?? [])]),
        tst: refs(sid, scripts, tests),
    };
}

// ---- bug reports (optional) -------------------------------------------------------------------------------------
const norm = s => s.toLowerCase().replace(/[^a-z0-9 ]+/g, ' ').replace(/\s+/g, ' ');

async function discord(token, method, route, body) {
    for (let attempt = 0; attempt < 5; attempt++) {
        const res = await fetch('https://discord.com/api/v10' + route, {
            method, headers: { Authorization: `Bot ${token}`, 'Content-Type': 'application/json', 'User-Agent': 'DiscordBot (ro3emu-class-status, 1.0)' },
            body: body ? JSON.stringify(body) : undefined,
        });
        if (res.status === 429) { await sleep(((await res.json()).retry_after ?? 2) * 1000 + 200); continue; }
        if (!res.ok) throw new Error(`${method} ${route}: ${res.status} ${(await res.text()).slice(0, 200)}`);
        return res.status === 204 ? null : res.json();
    }
    throw new Error(`${method} ${route}: rate limited`);
}
const sleep = ms => new Promise(r => setTimeout(r, ms));

async function loadReports(token, guild, forum) {
    const channel = await discord(token, 'GET', `/channels/${forum}`);
    const tagName = new Map(channel.available_tags.map(t => [t.id, t.name]));
    const state = n => ({ Investigating: 'open', 'Need more info': 'open', 'Fix coming': 'open', Fixed: 'fixed', 'Not a bug': 'notbug' })[n];
    const threads = (await discord(token, 'GET', `/guilds/${guild}/threads/active`)).threads.filter(t => t.parent_id === forum);
    for (let before = ''; ;) {
        const page = await discord(token, 'GET', `/channels/${forum}/threads/archived/public?limit=100${before}`);
        threads.push(...page.threads.filter(t => !threads.some(x => x.id === t.id)));
        if (!page.has_more || !page.threads.length) break;
        before = `&before=${page.threads.at(-1).thread_metadata.archive_timestamp}`;
    }
    const reports = [];
    for (const t of threads) {
        const first = await discord(token, 'GET', `/channels/${t.id}/messages?limit=1&after=0`);
        const names = (t.applied_tags ?? []).map(id => tagName.get(id));
        reports.push({
            id: t.id, title: t.name, status: names.map(state).find(Boolean) ?? 'none',
            lines: new Set(names.filter(n => n?.endsWith(' line')).map(n => n.replace(' line', ''))),
            text: norm(t.name + ' ' + (first[0]?.content ?? '').slice(0, 1500)),
        });
        await sleep(120);
    }
    return reports.filter(r => !r.title.startsWith('📌'));
}

function matchReports(reports, skillName, line) {
    const n = norm(skillName).trim();
    if (n.length < 5) return [];
    const keys = [n, ...(ALIAS[n] ?? [])];
    return reports.filter(r => (!r.lines.size || r.lines.has(line))
        && keys.some(k => new RegExp(`(?<![a-z0-9])${k.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?![a-z0-9])`).test(r.text)));
}

// ---- status and rendering ------------------------------------------------------------------------------------------
const isOpen = r => r.status === 'open' || (r.status === 'none' && !r.title.toLowerCase().includes('suggestion'));

function statusOf(a, reps) {
    if (reps.some(isOpen)) return 'open';
    if (!(a.dmg || a.buff || a.attri) && !a.src.length && !a.tst.length) return 'nothing';
    if (reps.some(r => r.status === 'fixed')) return 'fixed';
    return a.tst.length ? 'tested' : a.src.length ? 'logic' : 'data';
}

function renderLine(line, jobs, reports, guild, date) {
    const counts = {}, body = [], used = new Set();
    const link = r => `[${r.title.slice(0, 42)}](<https://discord.com/channels/${guild}/${r.id}>)`;
    for (const [jobName, skills] of jobs) {
        const section = [`**${jobName}**`];
        const evaluated = skills.map(a => {
            const reps = matchReports(reports, a.name, line);
            reps.forEach(r => used.add(r.id));
            return { a, reps, st: statusOf(a, reps) };
        });
        for (const { a, reps, st } of evaluated) {
            // a second row of the same name (a level-1 helper) is not a skill of its own
            if (st === 'nothing' && evaluated.filter(e => e.a.name === a.name).length > 1) continue;
            counts[st] = (counts[st] ?? 0) + 1;
            const note = st === 'open' ? ' ' + reps.filter(r => r.status !== 'fixed').map(link).join(', ').slice(0, 170)
                : st === 'fixed' ? ' *(fixed after a report)*' : '';
            section.push(`${ICON[st]} ${a.name}${a.active ? '' : ' *(passive)*'}${note}`);
        }
        body.push(section.join('\n'));
    }
    const loose = reports.filter(r => r.lines.has(line) && !used.has(r.id) && isOpen(r));
    if (loose.length) body.push('**Other open reports for this class line**\n'
        + loose.map(r => `🔴 [${r.title.slice(0, 60)}](<https://discord.com/channels/${guild}/${r.id}>)`).join('\n'));
    const c = k => counts[k] ?? 0;
    const head = `📊 **${line} line: skill status** (${Object.values(counts).reduce((x, y) => x + y, 0)} skills)\n${INTRO(date)}\n\n${LEGEND}`
        + `\n\n**At a glance:** 🧪 ${c('tested')}  🟢 ${c('logic')}  🟡 ${c('data')}  ✅ ${c('fixed')}  🔴 ${c('open')}  ⚪ ${c('nothing')}`;
    const messages = [head];
    for (const s of body) {
        if (messages.length > 1 && messages.at(-1).length + s.length + 2 <= 1900) messages[messages.length - 1] += '\n\n' + s;
        else messages.push(s);
    }
    if (messages.some(m => m.length > 2000)) throw new Error(`${line}: a message is over Discord's 2000 characters`);
    return messages;
}

// ---- posting: edit the bot's messages in the status channel in place ---------------------------------------------
async function post(token, channel, messages) {
    const me = await discord(token, 'GET', '/users/@me');
    const mine = (await discord(token, 'GET', `/channels/${channel}/messages?limit=100`)).filter(m => m.author.id === me.id).reverse();
    if (mine.length === messages.length) {
        let edited = 0;
        for (const [i, m] of mine.entries()) if (m.content !== messages[i]) { await discord(token, 'PATCH', `/channels/${channel}/messages/${m.id}`, { content: messages[i], allowed_mentions: { parse: [] } }); edited++; await sleep(300); }
        console.log(`${edited} of ${mine.length} messages edited`);
        return;
    }
    for (const m of mine) { await discord(token, 'DELETE', `/channels/${channel}/messages/${m.id}`); await sleep(300); }
    for (const text of messages) { await discord(token, 'POST', `/channels/${channel}/messages`, { content: text, flags: 4, allowed_mentions: { parse: [] } }); await sleep(600); }
    console.log(`${messages.length} messages posted`);
}

// ---- main -------------------------------------------------------------------------------------------------------
async function main() {
    const args = process.argv.slice(2);
    const out = args.includes('--out') ? args[args.indexOf('--out') + 1] : null;
    const { DISCORD_BOT_TOKEN: token, DISCORD_GUILD: guild, DISCORD_BUG_FORUM: forum, DISCORD_STATUS_CHANNEL: statusChannel } = process.env;
    if (!fs.existsSync(CONFIG)) throw new Error('data/config is missing: export the game data first (README, "Export the game data")');
    if (args.includes('--post') && !(token && statusChannel)) throw new Error('--post needs DISCORD_BOT_TOKEN and DISCORD_STATUS_CHANNEL');

    const data = loadData();
    const src = corpus(path.join(REPO, 'src', 'Ro3Emu.Server'), [path.join('Data', 'Config', 'Tables')]);
    const tests = corpus(path.join(REPO, 'tests', 'Ro3Emu.Tests'), []);
    const reports = token && guild && forum ? await loadReports(token, guild, forum) : [];
    if (!reports.length) console.error('No bug reports read (set DISCORD_BOT_TOKEN, DISCORD_GUILD and DISCORD_BUG_FORUM): no skill will show as open or fixed.');
    const date = new Date().toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' });

    const messages = [];
    for (const [n, line] of Object.entries(LINES)) {
        const jobs = [...data.tree].filter(([job]) => job >= 1100 && Math.floor(job / 1000) === Number(n)).sort((a, b) => a[0] - b[0])
            .map(([job, ids]) => [data.jobName.get(job), [...ids].sort((a, b) => a - b).map(id => analyse(id, data, src, tests))]);
        messages.push(...renderLine(line, jobs, reports, guild ?? '0', date));
    }
    if (args.includes('--post')) await post(token, statusChannel, messages);
    const preview = messages.join('\n\n---\n\n') + '\n';
    if (out) fs.writeFileSync(out, preview);
    else if (!args.includes('--post')) process.stdout.write(preview);
}

main().catch(e => { console.error(e.message); process.exit(1); });
