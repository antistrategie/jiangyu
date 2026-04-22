import { useEffect, useRef, useState } from "react";
import * as THREE from "three";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { Spinner } from "../Spinner/Spinner.tsx";
import styles from "./AssetBrowser.module.css";

interface ModelViewerProps {
  dataUrl: string;
}

export function ModelViewer({ dataUrl }: ModelViewerProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const cleanupRef = useRef<(() => void) | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    cleanupRef.current?.();
    setLoading(true);

    let cancelled = false;

    // Defer all heavy work so the browser can paint the spinner first.
    const frameId = requestAnimationFrame(() => {
      if (cancelled) return;

      const w = container.clientWidth;
      const h = container.clientHeight;

      const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
      renderer.setSize(w, h);
      renderer.setPixelRatio(window.devicePixelRatio);
      renderer.setClearColor(0x000000, 0);
      renderer.toneMapping = THREE.ACESFilmicToneMapping;
      renderer.toneMappingExposure = 1.8;
      container.appendChild(renderer.domElement);

      const scene = new THREE.Scene();
      const camera = new THREE.PerspectiveCamera(45, w / h, 0.01, 1000);

      const controls = new OrbitControls(camera, renderer.domElement);
      controls.enableDamping = true;
      controls.dampingFactor = 0.1;
      controls.autoRotate = true;
      controls.autoRotateSpeed = 2;

      const stopAutoRotate = () => {
        controls.autoRotate = false;
      };
      controls.addEventListener("start", stopAutoRotate);

      // Lighting
      const ambient = new THREE.AmbientLight(0xffffff, 3.0);
      scene.add(ambient);
      const hemi = new THREE.HemisphereLight(0xffffff, 0x888888, 2.0);
      scene.add(hemi);
      const key = new THREE.DirectionalLight(0xffffff, 3.0);
      key.position.set(5, 10, 7);
      scene.add(key);
      const fill = new THREE.DirectionalLight(0xffffff, 2.0);
      fill.position.set(-5, 5, -5);
      scene.add(fill);
      const rim = new THREE.DirectionalLight(0xffffff, 1.5);
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

      innerCleanup = () => {
        cancelAnimationFrame(animId);
        resizeObserver.disconnect();
        controls.removeEventListener("start", stopAutoRotate);
        controls.dispose();
        renderer.forceContextLoss();
        renderer.dispose();
        if (renderer.domElement.parentNode === container) {
          container.removeChild(renderer.domElement);
        }
      };

      void (async () => {
        try {
          const resp = await fetch(dataUrl);
          const buffer = await resp.arrayBuffer();
          if (cancelled) return;

          const loader = new GLTFLoader();
          const gltf = await loader.parseAsync(buffer, "");
          if (cancelled) return;

          scene.add(gltf.scene);

          const box = new THREE.Box3().setFromObject(gltf.scene);
          const size = box.getSize(new THREE.Vector3());
          const center = box.getCenter(new THREE.Vector3());

          const maxDim = Math.max(size.x, size.y, size.z);
          const fov = camera.fov * (Math.PI / 180);
          const cameraDistance = (maxDim / (2 * Math.tan(fov / 2))) * 1.5;

          camera.position.set(
            center.x + cameraDistance * 0.5,
            center.y + cameraDistance * 0.5,
            center.z + cameraDistance,
          );
          camera.lookAt(center);
          controls.target.copy(center);
          controls.update();
        } catch (err) {
          if (!cancelled) console.error("[ModelViewer] GLB load error:", err);
        } finally {
          if (!cancelled) setLoading(false);
        }
      })();
    });

    let innerCleanup: (() => void) | undefined;

    const cleanup = () => {
      cancelled = true;
      cancelAnimationFrame(frameId);
      innerCleanup?.();
    };
    cleanupRef.current = cleanup;
    return cleanup;
  }, [dataUrl]);

  return (
    <div ref={containerRef} className={styles.modelViewer}>
      {loading && <Spinner className={styles.modelViewerSpinner} />}
    </div>
  );
}
