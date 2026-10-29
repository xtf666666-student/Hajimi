import { addPet, listPets, removePet, catalog } from "./installer.js";

const helpText = `用法:
  dsh-pet-installer add <pet-id>
  dsh-pet-installer list
  dsh-pet-installer remove <pet-id>

选项:
  -h, --help    显示帮助`;

export async function runCli(args) {
  const [command, value, ...extra] = args;

  if (!command || command === "--help" || command === "-h") {
    console.log(helpText);
    return 0;
  }

  try {
    if (command === "list") {
      const pets = await listPets();
      if (pets.length === 0) {
        console.log("还没有安装桌宠。");
        console.log(`可用：${Object.keys(catalog).join(", ")}`);
        return 0;
      }
      console.log(`已安装 ${pets.length} 个：`);
      for (const p of pets) {
        console.log(`  ${p.displayName} (${p.id})`);
        console.log(`    ${p.installPath}`);
      }
      return 0;
    }

    if (command !== "add" && command !== "remove") {
      throw new Error(`未知命令：${command}`);
    }
    if (!value || extra.length > 0) {
      throw new Error(`用法: dsh-pet-installer ${command} <pet-id>`);
    }

    if (command === "add") {
      const r = await addPet({ petSlug: value });
      console.log(`已安装 ${r.displayName} (${r.id})`);
      console.log(r.installPath);
    } else {
      const r = await removePet({ petSlug: value });
      console.log(`已卸载 ${r.id}`);
      console.log(r.installPath);
    }
    return 0;
  } catch (err) {
    console.error(`错误：${err?.message ?? err}`);
    return 1;
  }
}