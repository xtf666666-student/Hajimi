#!/usr/bin/env node
'use strict';

const { run } = require('../dist/cli.js');

run(process.argv.slice(2))
  .then((code) => {
    process.exitCode = code;
  })
  .catch((error) => {
    console.error(`哈基米桌宠 CLI 运行失败：${error && error.stack ? error.stack : error}`);
    process.exitCode = 1;
  });
