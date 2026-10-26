import { checkUpdate, install, list, shellHint, uninstall, update } from './installer';
import { color, detail, info, title, warn } from './ui';

const VERSION: string = (require('../package.json') as { version: string }).version;
const DEFAULT_PET = 'hajimi-desktop-pet';

export const HELP = `${color.bold('哈基米桌宠 CLI')} ${color.dim(`v${VERSION}`)}

用法：
  npx hajimi-pet-cli <命令> [桌宠ID] [选项]

命令：
  install [id]      下载并安装桌宠（默认 ${DEFAULT_PET}）
  update  [id]      把已安装的桌宠更新到最新版本
  uninstall [id]    卸载桌宠并清理快捷方式
  list              查看本机已安装的桌宠
  check  [id]       只检查是否有新版本
  help              显示本帮助

选项：
  --release <版本>   安装指定版本（默认最新）
  --desktop          同时创建桌面快捷方式
  --no-shortcut      不创建开始菜单/桌面快捷方式
  --force            已安装同版本时强制重新下载安装
  --installer        使用官方 NSIS 安装包（默认使用便携版，不执行任何脚本）
  -y, --yes          跳过安装包运行前的确认
  --verbose          输出注册表、校验和等调试信息
  -h, --help         显示帮助
  -v, --version      显示版本

示例：
  npx hajimi-pet-cli install
  npx hajimi-pet-cli install hajimi-desktop-pet --desktop
  npx hajimi-pet-cli list
  npx hajimi-pet-cli update
  npx hajimi-pet-cli uninstall hajimi-desktop-pet

说明：
  · 安装包来自 GitHub Releases，安装前会校验 SHA-256；
  · 默认安装到 %LOCALAPPDATA%\\HajimiPet\\pets\\<id>\\<版本>；
  · 桌面宠物本体不需要 Node.js，只有本安装器需要。`;

interface ParsedArgs {
  command: string;
  petId?: string;
  release?: string;
  desktop: boolean;
  noShortcut: boolean;
  force: boolean;
  installer: boolean;
  yes: boolean;
  verbose: boolean;
  help: boolean;
  version: boolean;
  json: boolean;
}

export function parseArgs(argv: string[]): ParsedArgs {
  const result: ParsedArgs = {
    command: '',
    desktop: false,
    noShortcut: false,
    force: false,
    installer: false,
    yes: false,
    verbose: false,
    help: false,
    version: false,
    json: false,
  };
  const positionals: string[] = [];

  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    switch (arg) {
      case '-h':
      case '--help':
        result.help = true;
        break;
      case '-v':
      case '--version':
        result.version = true;
        break;
      case '--desktop':
        result.desktop = true;
        break;
      case '--no-shortcut':
        result.noShortcut = true;
        break;
      case '--force':
        result.force = true;
        break;
      case '--installer':
        result.installer = true;
        break;
      case '-y':
      case '--yes':
        result.yes = true;
        break;
      case '--verbose':
        result.verbose = true;
        break;
      case '--json':
        result.json = true;
        break;
      case '--release': {
        const value = argv[i + 1];
        if (!value || value.startsWith('-')) throw new Error('--release 需要一个版本号');
        result.release = value;
        i += 1;
        break;
      }
      default:
        if (arg.startsWith('--release=')) {
          result.release = arg.slice('--release='.length);
        } else if (arg.startsWith('-')) {
          throw new Error(`未知选项：${arg}`);
        } else {
          positionals.push(arg);
        }
        break;
    }
  }

  result.command = positionals[0] ?? '';
  result.petId = positionals[1];
  return result;
}

export async function run(argv: string[]): Promise<number> {
  let args: ParsedArgs;
  try {
    args = parseArgs(argv);
  } catch (error) {
    warn(String(error));
    console.log(`\n${HELP}`);
    return 1;
  }

  if (args.version && !args.command) {
    console.log(VERSION);
    return 0;
  }
  if (args.help || !args.command || args.command === 'help') {
    console.log(`\n${HELP}\n`);
    return 0;
  }

  const petId = args.petId ?? DEFAULT_PET;
  try {
    switch (args.command) {
      case 'install':
        title(`安装 ${petId}`);
        return await install({
          petId,
          release: args.release,
          kind: args.installer ? 'installer' : 'portable',
          desktopShortcut: args.desktop,
          noShortcut: args.noShortcut,
          force: args.force,
          yes: args.yes,
          verbose: args.verbose,
        });
      case 'update':
        title(`更新 ${petId}`);
        return await update(petId, {
          desktopShortcut: args.desktop,
          noShortcut: args.noShortcut,
          force: args.force,
          yes: args.yes,
          verbose: args.verbose,
        });
      case 'uninstall':
        title(`卸载 ${petId}`);
        return await uninstall(petId);
      case 'list':
        title('本机已安装桌宠');
        if (args.json) {
          shellHint();
          return 0;
        }
        return list(args.verbose);
      case 'check':
        title(`检查更新 ${petId}`);
        return await checkUpdate(petId, args.verbose);
      default:
        warn(`未知命令：${args.command}`);
        info('可用命令：install / update / uninstall / list / check / help');
        return 1;
    }
  } catch (error) {
    console.error(`${color.red('✖')} ${color.red(error instanceof Error ? error.message : String(error))}`);
    if (args.verbose && error instanceof Error && error.stack) {
      detail(error.stack);
    }
    return 1;
  }
}

const isDirectRun = require.main === module;
if (isDirectRun) {
  void run(process.argv.slice(2)).then((code) => {
    process.exitCode = code;
  });
}
