# D2R Reimagined

## Installation
Clone repository to get started

### Electron
NOTE: electron dev script relies on `nodemon` to listen for file changes. Instead of installing nodemon as a package install it globally with the `-g` flag.

https://www.npmjs.com/package/nodemon
```
pnpm i -g nodemon
```

To run dev server. Execute following command in your terminal from the 
root directory 
```
pnpm run electron
```
or to launch electron with devtools open
```
pnpm run electron:devtools
```

### Frontend
To spawn frontend dev server simply cd into `frontend-app`, install and launch the dev script
```
cd frontend-app
pnpm install
pnpm run dev
```

## Build
Launching the electron build will first build the `frontend-app` before being packaged to an electron application.

From root directory. Execute following build script
```
pnpm run build
```

The build step is managed by the [electron-builder](https://www.electron.build/) library. To configure the build step, refer to it's documentation.

NOTE: Currently only windows configured out of the box.

## Frontend libraries used
This boilerplate is customized by me for me so and the reason why these packages is included by default.

* https://github.com/hjalmar/enums-manager
* https://github.com/hjalmar/hotkeys-manager
* https://github.com/hjalmar/svelte-standalone-router
