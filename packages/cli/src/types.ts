export interface PlatformAsset {
  /** 便携版 .exe 下载地址 */
  portable?: { url: string; sha256?: string };
  /** NSIS 安装包下载地址 */
  installer?: { url: string; sha256?: string };
}

export interface PetRelease {
  version: string;
  releasedAt?: string;
  notes?: string;
  assets: Record<string, PlatformAsset>;
}

export interface PetEntry {
  id: string;
  name: string;
  description?: string;
  repo: string;
  homepage?: string;
  latest: string;
  releases: Record<string, PetRelease>;
}

export interface Registry {
  version: number;
  updatedAt?: string;
  pets: PetEntry[];
}

export interface InstalledVersion {
  version: string;
  path: string;
  installedAt: string;
  kind: 'portable' | 'installer';
  sha256?: string;
}

export interface InstalledPet {
  id: string;
  name: string;
  currentVersion: string;
  installs: InstalledVersion[];
  shortcuts: string[];
}

export interface InstalledState {
  version: number;
  pets: Record<string, InstalledPet>;
}

export interface ResolvedAsset {
  version: string;
  url: string;
  sha256?: string;
  kind: 'portable' | 'installer';
  fileName: string;
}
