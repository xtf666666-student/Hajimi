/**
 * dsh-pet-installer 端到端测试（仿 codex-pet-installer 的结构）。
 *
 * 用 DSH_HOME 指向临时目录，真实跑 add / list / remove，
 * 并校验落地的 atlas 尺寸符合 Codex 契约。
 */
import { after, before, test } from "node:test";
import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, rmSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..");
const bin = join(root, "bin", "dsh-pet-installer.js");

let home;

function runCli(args) {
  return new Promise((resolve) => {
    execFile(
      process.execPath,
      [bin, ...args],
      { env: { ...process.env, DSH_HOME: home, NO_COLOR: "1" }, timeout: 60000 },
      (error, stdout, stderr) => {
        resolve({ code: error ? (error.code ?? 1) : 0, stdout, stderr });
      },
    );
  });
}

/** 读 PNG 的 IHDR，拿真实宽高（不引第三方解码器）。 */
function pngSize(path) {
  const buf = readFileSync(path);
  // PNG: 8 字节签名 + 4 字节长度 + "IHDR" + width(4) + height(4)
  assert.equal(buf.subarray(1, 4).toString("ascii"), "PNG", "应为 PNG");
  const w = buf.readUInt32BE(16);
  const h = buf.readUInt32BE(20);
  return { w, h };
}

before(() => {
  home = mkdtempSync(join(tmpdir(), "dsh-pet-"));
});

after(() => {
  rmSync(home, { recursive: true, force: true });
});

test("--help 显示用法", async () => {
  const r = await runCli(["--help"]);
  assert.equal(r.code, 0);
  assert.match(r.stdout, /add/);
  assert.match(r.stdout, /list/);
  assert.match(r.stdout, /remove/);
});

test("未收录的桌宠报错并列出可用项", async () => {
  const r = await runCli(["add", "not-a-real-pet"]);
  assert.equal(r.code, 1);
  assert.match(r.stdout + r.stderr, /未收录|可用/);
});

test("非法 slug 被拒", async () => {
  const r = await runCli(["add", "Bad_Name"]);
  assert.equal(r.code, 1);
  assert.match(r.stdout + r.stderr, /小写/);
});

test("add 会把 pet.json 与 spritesheet 落到 DSH_HOME/pets", async () => {
  const r = await runCli(["add", "hajimi-desktop-pet"]);
  assert.equal(r.code, 0, r.stdout + r.stderr);

  const dir = join(home, "pets", "hajimi-desktop-pet");
  assert.ok(existsSync(join(dir, "pet.json")), "缺 pet.json");
  assert.ok(existsSync(join(dir, "spritesheet.webp")), "缺 spritesheet.webp");

  const manifest = JSON.parse(readFileSync(join(dir, "pet.json"), "utf8"));
  assert.equal(manifest.id, "hajimi-desktop-pet");
  assert.ok(manifest.displayName.length > 0);
  assert.ok(manifest.description.length > 0);
  assert.equal(manifest.spritesheetPath, "spritesheet.webp");
});

test("同时安装 PNG 供原生渲染读取", async () => {
  const png = join(home, "pets", "hajimi-desktop-pet", "spritesheet.png");
  assert.ok(existsSync(png), "缺 spritesheet.png（GDI+ 解不了 WebP）");
  const { w, h } = pngSize(png);
  assert.equal(w, 1536, "atlas 宽应为 1536");
  assert.equal(h, 1872, "atlas 高应为 1872");
  assert.ok(statSync(png).size > 100 * 1024, "atlas 不应是空图");
});

test("WebP 也符合契约尺寸（VP8X 头）", async () => {
  const webp = join(home, "pets", "hajimi-desktop-pet", "spritesheet.webp");
  const buf = readFileSync(webp);
  assert.equal(buf.subarray(0, 4).toString("ascii"), "RIFF");
  assert.equal(buf.subarray(8, 12).toString("ascii"), "WEBP");
  // VP8X：宽度/高度各 3 字节（24 位，减 1 存）
  const chunk = buf.subarray(12, 16).toString("ascii");
  if (chunk === "VP8X") {
    const w = buf.readUIntLE(24, 3) + 1;
    const h = buf.readUIntLE(27, 3) + 1;
    assert.equal(w, 1536, "WebP 宽应为 1536");
    assert.equal(h, 1872, "WebP 高应为 1872");
  }
});

test("list 展示已安装桌宠", async () => {
  const r = await runCli(["list"]);
  assert.equal(r.code, 0);
  assert.match(r.stdout, /哈基米桌宠/);
  assert.match(r.stdout, /hajimi-desktop-pet/);
});

test("remove 清空安装目录", async () => {
  const r = await runCli(["remove", "hajimi-desktop-pet"]);
  assert.equal(r.code, 0);
  assert.equal(existsSync(join(home, "pets", "hajimi-desktop-pet")), false);
});

test("移除后再 list 为空", async () => {
  const r = await runCli(["list"]);
  assert.equal(r.code, 0);
  assert.match(r.stdout, /还没有安装/);
});

test("缺参数时报用法", async () => {
  const r = await runCli(["add"]);
  assert.equal(r.code, 1);
  assert.match(r.stdout + r.stderr, /用法/);
});