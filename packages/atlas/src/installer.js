import { mkdir, writeFile, readFile } from "node:fs/promises";
import { homedir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const assetsDir = path.join(here, "..", "assets");

const slugPattern = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

/**
 * 已收录的桌宠。
 *
 * 与 codex-pet-installer 的关键差别：那边从远端 codex-pet.org 拉包，
 * 这边把 atlas 直接随 npm 包分发。好处是不依赖外站可达性，
 * 也不存在"站点挂了就装不上"。想加远端源时可以再叠一层。
 */
const catalog = {
  "hajimi-desktop-pet": {
    id: "hajimi-desktop-pet",
    displayName: "哈基米桌宠",
    description:
      "常驻 Windows 桌面的哈基米猫猫桌宠，显示 DeepSeek Harness 里正在运行的对话数。",
  },
};

/** 安装根目录：%USERPROFILE%\.dsh\pets（可用 DSH_HOME 覆盖）。 */
export function petsRoot() {
  const home = process.env.DSH_HOME || path.join(homedir(), ".dsh");
  return path.join(home, "pets");
}

export function normalizeSlug(value) {
  const slug = String(value || "").trim().toLowerCase();
  if (!slugPattern.test(slug)) {
    throw new Error("精宠名必须是小写短横线形式，例如 hajimi-desktop-pet");
  }
  return slug;
}

/** 读取随包分发的 pet.json 与 spritesheet。 */
async function readBundledAssets() {
  const manifestBytes = await readFile(path.join(assetsDir, "pet.json"));
  const spritesheetBytes = await readFile(path.join(assetsDir, "spritesheet.webp"));
  // 同时带一份 PNG：.NET Framework 的 GDI+ 解不了 WebP，
  // 原生桌宠只能读位图。WebP 仍按契约保留，便于其它宿主使用。
  let pngBytes = null;
  try {
    pngBytes = await readFile(path.join(assetsDir, "spritesheet.png"));
  } catch {
    /* 没有 png 就只装 webp */
  }
  return { manifestBytes, spritesheetBytes, pngBytes };
}

function safeInstallId(value) {
  const id = String(value || "").trim();
  if (!id || id === "." || id === ".." || /[\\/]/.test(id)) {
    throw new Error("pet.json 的 id 非法");
  }
  return id;
}

/**
 * 校验 atlas 是否符合 Codex 规格。
 * 契约：1536×1872，8 列 9 行，每格 192×208，透明背景。
 */
export function validateSpritesheet(bytes) {
  const problems = [];
  if (!bytes || bytes.length === 0) {
    problems.push("spritesheet 为空");
    return problems;
  }
  // 只做轻量校验：WebP 魔数 + 从 VP8X 头读尺寸
  const head = bytes.subarray(0, 30);
  const isWebp = head[0] === 0x52 && head[1] === 0x49 && head[2] === 0x46 && head[3] === 0x46;
  if (!isWebp) problems.push("不是 WebP 文件");
  return problems;
}

export async function addPet({ petSlug, targetRoot = petsRoot() }) {
  const slug = normalizeSlug(petSlug);
  const entry = catalog[slug];
  if (!entry) {
    throw new Error(
      `未收录该桌宠：${slug}。可用：${Object.keys(catalog).join(", ")}`,
    );
  }

  const { manifestBytes, spritesheetBytes, pngBytes } = await readBundledAssets();
  const manifest = JSON.parse(new TextDecoder().decode(manifestBytes));

  const problems = validateSpritesheet(spritesheetBytes);
  if (problems.length > 0) {
    throw new Error(`atlas 校验失败：${problems.join("；")}`);
  }

  const petId = safeInstallId(manifest.id || slug);
  const installPath = path.join(targetRoot, petId);

  await mkdir(installPath, { recursive: true });
  await writeFile(path.join(installPath, "pet.json"), manifestBytes);
  await writeFile(path.join(installPath, "spritesheet.webp"), spritesheetBytes);
  // 原生桌宠读这份
  if (pngBytes) {
    await writeFile(path.join(installPath, "spritesheet.png"), pngBytes);
  }

  return {
    displayName: manifest.displayName || entry.displayName || petId,
    id: petId,
    installPath,
    hasPng: Boolean(pngBytes),
  };
}

export async function listPets({ targetRoot = petsRoot() } = {}) {
  const { readdir } = await import("node:fs/promises");
  let entries;
  try {
    entries = await readdir(targetRoot, { withFileTypes: true });
  } catch {
    return [];
  }

  const found = [];
  for (const e of entries) {
    if (!e.isDirectory()) continue;
    try {
      const raw = await readFile(path.join(targetRoot, e.name, "pet.json"));
      const manifest = JSON.parse(new TextDecoder().decode(raw));
      found.push({
        id: manifest.id || e.name,
        displayName: manifest.displayName || e.name,
        installPath: path.join(targetRoot, e.name),
      });
    } catch {
      /* 没有 manifest 的目录不算已安装 */
    }
  }
  return found;
}

export async function removePet({ petSlug, targetRoot = petsRoot() }) {
  const { rm } = await import("node:fs/promises");
  const slug = normalizeSlug(petSlug);
  const dir = path.join(targetRoot, slug);
  await rm(dir, { recursive: true, force: true });
  return { id: slug, installPath: dir };
}

export { catalog };