// 결과 3D 미리보기 (three.js). GLB/GLTF/OBJ 만 — 그 외 형식은 폴더에서 연다.
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { OBJLoader } from 'three/addons/loaders/OBJLoader.js';

export const MODEL_EXTENSIONS = ['.glb', '.gltf', '.obj'];

export class Viewer {
    constructor(canvas) {
        this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true });
        this.renderer.setPixelRatio(window.devicePixelRatio);
        this.renderer.outputColorSpace = THREE.SRGBColorSpace;

        this.scene = new THREE.Scene();
        this.camera = new THREE.PerspectiveCamera(40, 1, 0.01, 1000);
        this.camera.position.set(2, 1.5, 3);
        this.controls = new OrbitControls(this.camera, canvas);
        this.controls.enableDamping = true;

        this.scene.add(new THREE.HemisphereLight(0xffffff, 0x404050, 2.2));
        const key = new THREE.DirectionalLight(0xffffff, 2.0);
        key.position.set(3, 5, 4);
        this.scene.add(key);
        const rim = new THREE.DirectionalLight(0x99bbff, 0.8);
        rim.position.set(-4, 2, -3);
        this.scene.add(rim);

        this.grid = new THREE.GridHelper(4, 20, 0x555555, 0x333333);
        this.scene.add(this.grid);

        this.model = null;
        this.mixer = null;
        this.clips = [];
        this.spin = false;
        this.wire = false;
        this.clock = new THREE.Clock();

        new ResizeObserver(() => this.resize()).observe(canvas.parentElement);
        this.resize();
        this.renderer.setAnimationLoop(() => this.tick());
    }

    resize() {
        const host = this.renderer.domElement.parentElement;
        const width = host.clientWidth;
        const height = host.clientHeight;
        if (0 === width || 0 === height) {
            return;
        }
        this.renderer.setSize(width, height, false);
        this.camera.aspect = width / height;
        this.camera.updateProjectionMatrix();
    }

    tick() {
        const delta = this.clock.getDelta();
        if (this.mixer) {
            this.mixer.update(delta);
        }
        if (this.spin && this.model) {
            this.model.rotation.y += delta * 0.6;
        }
        this.controls.update();
        this.renderer.render(this.scene, this.camera);
    }

    // 성공 시 {triangles, vertices, animations}
    async load(url, ext) {
        this.clear();
        let object;
        let animations = [];
        if ('.obj' === ext) {
            object = await new OBJLoader().loadAsync(url);
        } else {
            const gltf = await new GLTFLoader().loadAsync(url);
            object = gltf.scene;
            animations = gltf.animations || [];
        }

        this.model = object;
        this.scene.add(object);
        this.clips = animations;
        if (animations.length > 0) {
            this.mixer = new THREE.AnimationMixer(object);
            this.playClip(0);
        }
        this.applyWire();
        this.fit();

        let triangles = 0;
        let vertices = 0;
        object.traverse((node) => {
            if (!node.isMesh) {
                return;
            }
            const geometry = node.geometry;
            const count = geometry.index ? geometry.index.count : geometry.attributes.position.count;
            triangles += count / 3;
            vertices += geometry.attributes.position.count;
        });
        return { triangles: Math.round(triangles), vertices, animations: animations.length, clipNames: animations.map((clip, i) => clip.name || `clip ${i + 1}`) };
    }

    // 동작 여러 개를 클립 여러 개로 한 파일에 담은 GLB 도 있다
    playClip(index) {
        if (!this.mixer || !this.clips[index]) {
            return;
        }
        this.mixer.stopAllAction();
        this.mixer.clipAction(this.clips[index]).reset().play();
    }

    clear() {
        if (!this.model) {
            return;
        }
        this.scene.remove(this.model);
        this.model.traverse((node) => {
            if (node.geometry) {
                node.geometry.dispose();
            }
            const materials = Array.isArray(node.material) ? node.material : [node.material];
            for (const material of materials) {
                if (material) {
                    material.dispose();
                }
            }
        });
        this.model = null;
        this.mixer = null;
    }

    // 모델을 바닥(y=0) 위·원점 중심에 두고 카메라를 크기에 맞춤
    fit() {
        if (!this.model) {
            return;
        }
        this.model.rotation.set(0, 0, 0);
        this.model.position.set(0, 0, 0);
        const box = new THREE.Box3().setFromObject(this.model);
        const size = box.getSize(new THREE.Vector3());
        const center = box.getCenter(new THREE.Vector3());
        this.model.position.set(-center.x, -box.min.y, -center.z);

        const radius = Math.max(size.x, size.y, size.z) || 1;
        this.grid.scale.setScalar(radius);
        const distance = radius / Math.tan(THREE.MathUtils.degToRad(this.camera.fov / 2)) * 0.9;
        this.controls.target.set(0, size.y / 2, 0);
        this.camera.position.set(distance * 0.6, size.y / 2 + distance * 0.35, distance * 0.75);
        this.camera.near = radius / 100;
        this.camera.far = radius * 100;
        this.camera.updateProjectionMatrix();
        this.controls.update();
    }

    toggleSpin() {
        this.spin = !this.spin;
        return this.spin;
    }

    toggleWire() {
        this.wire = !this.wire;
        this.applyWire();
        return this.wire;
    }

    applyWire() {
        if (!this.model) {
            return;
        }
        this.model.traverse((node) => {
            const materials = Array.isArray(node.material) ? node.material : [node.material];
            for (const material of materials) {
                if (material && 'wireframe' in material) {
                    material.wireframe = this.wire;
                }
            }
        });
    }
}
