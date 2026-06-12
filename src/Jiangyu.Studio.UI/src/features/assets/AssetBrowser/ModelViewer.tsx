import { useCallback, useEffect, useRef, useState } from "react";
import * as THREE from "three";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { RoomEnvironment } from "three/addons/environments/RoomEnvironment.js";
import { Spinner } from "@shared/ui/Spinner/Spinner";
import styles from "./AssetBrowser.module.css";

interface ModelViewerProps {
  // base64 GLB bytes, not a data: URL — WebKitGTK blocks fetch() on large
  // data: URLs, so the viewer decodes the bytes itself.
  base64: string;
}

// The persistent pieces of the viewer that the per-asset load needs: the
// scene to swap the glTF root in and out of, plus the camera/controls to
// reframe around each new model.
interface Rig {
  readonly scene: THREE.Scene;
  readonly camera: THREE.PerspectiveCamera;
  readonly controls: OrbitControls;
  root: THREE.Object3D | null;
}

// Three's own discriminant flag; `instanceof THREE.Mesh` narrows to
// `Mesh<any, any, any>`, which the unsafe-assignment lint rejects.
const isMesh = (obj: THREE.Object3D): obj is THREE.Mesh =>
  (obj as Partial<THREE.Mesh>).isMesh === true;

// The WebGL context persists across asset swaps, so each outgoing glTF root
// must release its own GPU resources rather than relying on context teardown.
function disposeRoot(root: THREE.Object3D): void {
  root.traverse((obj) => {
    if (!isMesh(obj)) return;
    obj.geometry.dispose();
    const materials = Array.isArray(obj.material) ? obj.material : [obj.material];
    for (const material of materials) {
      for (const value of Object.values(material) as unknown[]) {
        if (value instanceof THREE.Texture) value.dispose();
      }
      material.dispose();
    }
  });
}

export function ModelViewer({ base64 }: ModelViewerProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const rigRef = useRef<Rig | null>(null);
  // A load requested before the mount effect's rAF has built the rig; the
  // rig-creation callback starts it.
  const pendingBase64Ref = useRef<string | null>(null);
  // Bumped per load request so stale async work bails out instead of
  // mutating the scene.
  const loadTokenRef = useRef(0);
  const [loading, setLoading] = useState(true);

  // Flip loading on each base64 change synchronously off prop identity so the
  // spinner shows immediately instead of one render after the effect fires.
  const [prevBase64, setPrevBase64] = useState(base64);
  if (prevBase64 !== base64) {
    setPrevBase64(base64);
    setLoading(true);
  }

  const loadModel = useCallback(async (rig: Rig, glb: string, token: number) => {
    // Stale once a newer load supersedes this one or the rig is torn down.
    const isStale = () => loadTokenRef.current !== token || rigRef.current !== rig;

    // Drop the outgoing model up front so the spinner overlays an empty
    // canvas rather than the previous asset.
    if (rig.root !== null) {
      rig.scene.remove(rig.root);
      disposeRoot(rig.root);
      rig.root = null;
    }
    try {
      const binary = atob(glb);
      const buffer = new ArrayBuffer(binary.length);
      const bytes = new Uint8Array(buffer);
      for (let i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
      }
      if (isStale()) return;

      const loader = new GLTFLoader();
      const gltf = await loader.parseAsync(buffer, "");
      if (isStale()) {
        disposeRoot(gltf.scene);
        return;
      }

      rig.scene.add(gltf.scene);
      rig.root = gltf.scene;

      const box = new THREE.Box3().setFromObject(gltf.scene);
      const size = box.getSize(new THREE.Vector3());
      const center = box.getCenter(new THREE.Vector3());

      const maxDim = Math.max(size.x, size.y, size.z);
      const fov = rig.camera.fov * (Math.PI / 180);
      const cameraDistance = (maxDim / (2 * Math.tan(fov / 2))) * 1.5;

      rig.camera.position.set(
        center.x + cameraDistance * 0.5,
        center.y + cameraDistance * 0.5,
        center.z + cameraDistance,
      );
      rig.camera.lookAt(center);
      rig.controls.target.copy(center);
      // Each fresh asset restarts the idle spin; user interaction stops it
      // again via the controls "start" listener.
      rig.controls.autoRotate = true;
      rig.controls.update();
    } catch (err) {
      if (!isStale()) console.error("[ModelViewer] GLB load error:", err);
    } finally {
      if (!isStale()) setLoading(false);
    }
  }, []);

  // Mount-once rig: renderer, scene, environment, lights, controls and
  // observers persist for the component's lifetime; per-asset loads only
  // swap the glTF root.
  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    let dispose: (() => void) | null = null;

    // Defer the heavy WebGL setup so the browser can paint the spinner first.
    const frameId = requestAnimationFrame(() => {
      const w = container.clientWidth;
      const h = container.clientHeight;

      const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
      renderer.setSize(w, h);
      renderer.setPixelRatio(window.devicePixelRatio);
      renderer.setClearColor(0x000000, 0);
      renderer.toneMapping = THREE.ACESFilmicToneMapping;
      renderer.toneMappingExposure = 1.1;
      container.appendChild(renderer.domElement);

      const scene = new THREE.Scene();
      const camera = new THREE.PerspectiveCamera(45, w / h, 0.01, 1000);

      // PBR materials render dark with directional lights alone — they need an
      // environment probe to fake indirect light. A PMREM'd RoomEnvironment is
      // the standard neutral-indoor rig; it lights every material consistently
      // whether or not the glTF ships explicit lights.
      const pmrem = new THREE.PMREMGenerator(renderer);
      const envTexture = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;
      scene.environment = envTexture;

      const controls = new OrbitControls(camera, renderer.domElement);
      controls.enableDamping = true;
      controls.dampingFactor = 0.1;
      controls.autoRotate = true;
      controls.autoRotateSpeed = 2;

      // Plain wheel scrolls the ancestor; Ctrl/Cmd + wheel zooms. Without this,
      // OrbitControls swallows every wheel event and the preview traps scroll.
      const wheelGuard = (e: WheelEvent) => {
        if (!e.ctrlKey && !e.metaKey) {
          e.stopImmediatePropagation();
        }
      };
      // eslint-disable-next-line @eslint-react/web-api-no-leaked-event-listener -- removed in the dispose closure below.
      renderer.domElement.addEventListener("wheel", wheelGuard, { capture: true });

      const stopAutoRotate = () => {
        controls.autoRotate = false;
      };
      // eslint-disable-next-line @eslint-react/web-api-no-leaked-event-listener -- removed in the dispose closure below.
      controls.addEventListener("start", stopAutoRotate);

      // Directional lights add shape on top of the environment probe.
      const key = new THREE.DirectionalLight(0xffffff, 2.5);
      key.position.set(5, 10, 7);
      scene.add(key);
      const fill = new THREE.DirectionalLight(0xffffff, 1.2);
      fill.position.set(-5, 5, -5);
      scene.add(fill);
      const rim = new THREE.DirectionalLight(0xffffff, 1.0);
      rim.position.set(0, -5, -10);
      scene.add(rim);

      let animId = 0;
      const animate = () => {
        animId = requestAnimationFrame(animate);
        controls.update();
        renderer.render(scene, camera);
      };
      animate();

      const resizeObserver = new ResizeObserver((entries) => {
        for (const entry of entries) {
          const { width, height } = entry.contentRect;
          if (width > 0 && height > 0) {
            camera.aspect = width / height;
            camera.updateProjectionMatrix();
            renderer.setSize(width, height);
          }
        }
      });
      resizeObserver.observe(container);

      const rig: Rig = { scene, camera, controls, root: null };
      rigRef.current = rig;

      dispose = () => {
        cancelAnimationFrame(animId);
        resizeObserver.disconnect();
        renderer.domElement.removeEventListener("wheel", wheelGuard, { capture: true });
        controls.removeEventListener("start", stopAutoRotate);
        controls.dispose();
        if (rig.root !== null) {
          scene.remove(rig.root);
          disposeRoot(rig.root);
          rig.root = null;
        }
        envTexture.dispose();
        pmrem.dispose();
        renderer.forceContextLoss();
        renderer.dispose();
        if (renderer.domElement.parentNode === container) {
          container.removeChild(renderer.domElement);
        }
      };

      // Start a load that arrived before the rig existed.
      const pending = pendingBase64Ref.current;
      if (pending !== null) {
        pendingBase64Ref.current = null;
        void loadModel(rig, pending, loadTokenRef.current);
      }
    });

    return () => {
      cancelAnimationFrame(frameId);
      // Nulling the rig marks any in-flight load stale via its rig check.
      rigRef.current = null;
      dispose?.();
    };
  }, [loadModel]);

  useEffect(() => {
    const token = ++loadTokenRef.current;
    const rig = rigRef.current;
    if (rig === null) {
      pendingBase64Ref.current = base64;
      return;
    }
    // Defer so the browser paints the spinner before the decode/parse work.
    const frameId = requestAnimationFrame(() => {
      void loadModel(rig, base64, token);
    });
    return () => {
      cancelAnimationFrame(frameId);
    };
  }, [base64, loadModel]);

  return (
    <div ref={containerRef} className={styles.modelViewer}>
      {loading && <Spinner className={styles.modelViewerSpinner} />}
      <div className={styles.viewerHint}>
        <span className={styles.viewerHintAction}>Orbit</span>
        <span className={styles.viewerHintControl}>left-click + drag</span>
        <span className={styles.viewerHintAction}>Pan</span>
        <span className={styles.viewerHintControl}>right-click + drag</span>
        <span className={styles.viewerHintAction}>Zoom</span>
        <span className={styles.viewerHintControl}>ctrl + wheel</span>
      </div>
    </div>
  );
}
