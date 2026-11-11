// StampGen 3D: three.js — только ОТОБРАЖЕНИЕ меша, собранного на СЕРВЕРЕ.
// © Guoud (iljamakeev@yandex.ru). Все права защищены. Условия: LICENSE.md.
// v1.6: СЕРВЕРНАЯ генерация. Раньше модель строил браузер
// (three-bvh-csg) — на слабом ПК вкладка «висла» и Chrome показывал
// «Страница не отвечает». Теперь C#-сервер (та же машина) строит весь меш и
// отдаёт браузеру готовые треугольники: браузер не считает ничего тяжёлого.
// =============================================================================

import * as THREE from 'three';
import { OrbitControls } from '../lib/three/OrbitControls.js';

let renderer, scene, camera, controls, container, contentGroup = null;
let dotnet = null;   // обратные вызовы в Blazor (потеря WebGL-контекста)
let needsRender = true;   // v1.6: рендер по требованию

// ================= инициализация =================
function init(elId, dotnetRef) {
    container = document.getElementById(elId);
    if (!container || renderer) { if (dotnetRef) dotnet = dotnetRef; return; }
    dotnet = dotnetRef || null;
    renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: false });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    container.appendChild(renderer.domElement);
    // если GPU/браузер сбросил контекст (тяжёлая сцена) — сообщаем в программу,
    // она остановит и покажет извинение вместо молчаливой пропажи картинки
    renderer.domElement.addEventListener('webglcontextlost', ev => {
        ev.preventDefault();
        try { dotnet && dotnet.invokeMethodAsync('View3dContextLost'); } catch (e) { }
    });
    scene = new THREE.Scene();
    scene.background = new THREE.Color(0xf1f4f8);
    camera = new THREE.PerspectiveCamera(45, 1, 0.1, 5000);
    camera.position.set(70, 110, 130);
    controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.dampingFactor = 0.12;
    const hemi = new THREE.HemisphereLight(0xffffff, 0x9aa7b5, 1.0);
    scene.add(hemi);
    const dir = new THREE.DirectionalLight(0xffffff, 1.5);
    dir.position.set(60, 120, 80);
    scene.add(dir);
    const dir2 = new THREE.DirectionalLight(0xdfe8f5, 0.5);
    dir2.position.set(-80, 60, -60);
    scene.add(dir2);
    const grid = new THREE.GridHelper(400, 40, 0xc4ccd6, 0xdfe4ea);
    grid.position.y = -0.05;
    scene.add(grid);
    const ro = new ResizeObserver(resize);
    ro.observe(container);
    resize();
    // v1.6: рендер по требованию — на слабых ПК постоянная перерисовка тяжёлой модели
    // зря грузила GPU/CPU. Рисуем только когда сцена изменилась (меш, поворот камеры,
    // инерция, ресайз).
    controls.addEventListener('change', () => { needsRender = true; });
    renderer.setAnimationLoop(() => {
        const moving = controls.update();   // true, пока гаснет инерция камеры
        if (moving || needsRender) {
            renderer.render(scene, camera);
            needsRender = false;
        }
    });
    container._sgNeedsRender = () => { needsRender = true; };
}

function resize() {
    if (!container) return;
    const w = container.clientWidth, h = container.clientHeight;
    if (w < 5 || h < 5) return;
    renderer.setSize(w, h, false);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
    needsRender = true;
}

function clearContent() {
    needsRender = true;
    if (!contentGroup) return;
    contentGroup.traverse(o => {
        if (o.geometry) o.geometry.dispose();
        if (o.material) o.material.dispose();
    });
    scene.remove(contentGroup);
    contentGroup = null;
}

// ================= показ меша с сервера =================
// streamRef — DotNetStreamReference с float32 xyz (треугольная плотка, мм);
// dims — { w, d, h } пластины для наведения камеры.
async function showMesh(streamRef, dims) {
    if (!renderer) init('view3d', null);
    const buf = await streamRef.arrayBuffer();
    clearContent();
    if (!buf || buf.byteLength < 36) return;
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(buf), 3));
    geo.computeVertexNormals();   // плоские нормали, как у CSG-меша раньше
    const mesh = new THREE.Mesh(geo, makePlateMaterial());
    contentGroup = new THREE.Group();
    contentGroup.add(mesh);
    scene.add(contentGroup);
    // камера по размеру пластины
    const w = (dims && dims.w) || 130, d = (dims && dims.d) || 80, h = (dims && dims.h) || 3;
    const maxDim = Math.max(w, d);
    camera.position.set(maxDim * 0.55, maxDim * 0.9, maxDim * 1.0);
    controls.target.set(0, h / 2, 0);
    controls.update();
    needsRender = true;   // v1.6: перерисовать новую модель
}

function makePlateMaterial() {
    return new THREE.MeshStandardMaterial({ color: 0x97a1ad, metalness: 0.25, roughness: 0.5 });
}

function clear() { clearContent(); }

window.stampGen3d = { init, showMesh, clear };
