import cv2
import zmq
import json
import time
from ultralytics import YOLO 

class AUBREngine:
    def __init__(self):
        # 1. إعداد الشبكة (ZMQ) للربط مع VB.NET
        self.context = zmq.Context()
        self.pub = self.context.socket(zmq.PUB)
        self.pub.bind("tcp://*:5556")
        self.rep = self.context.socket(zmq.REP)
        self.rep.bind("tcp://*:5555")
        
        # 2. إعداد الكاميرا
        self.cap = cv2.VideoCapture(0)
        
        # 3. تحميل نموذج YOLO الذكي
        # هذا النموذج سيتعرف على الشخص، الجوال، الطائرات، إلخ.
        self.model = YOLO('yolov8n.pt') 
        
        self.tracker = None
        self.is_tracking = False # حالة الإطباق (المربع الأخضر)
        self.target_box = None
        
        # إحداثيات المعترض والمهاجم في الفضاء الثلاثي الأبعاد
        self.int_x, self.int_y, self.int_z = 0.0, 0.0, 5.0
        self.tgt_x, self.tgt_y, self.tgt_z = 0.0, 0.0, 5.0
        
        # الحالة التكتيكية الافتراضية
        self.current_action = "IDLE"

    def run(self):
        print("--- AUBR AI Engine: SYSTEMS FULLY ACTIVE ---")
        while True:
            ret, frame = self.cap.read()
            if not ret: break
            h, w, _ = frame.shape
            
            # أ. مرحلة التعرف الذكي (YOLO Detection)
            # نستخدم imgsz=320 لتقليل التأخير وضمان سلاسة العرض
            results = self.model(frame, imgsz=320, verbose=False)
            
            for result in results:
                for box in result.boxes:
                    # الحصول على الإحداثيات واسم الجسم
                    x1, y1, x2, y2 = [int(v) for v in box.xyxy[0]]
                    cls = int(box.cls[0])
                    name = self.model.names[cls]
                    
                    # تمييز الأهداف المكتشفة بلون رمادي ونص واضح
                    cv2.rectangle(frame, (x1, y1), (x2, y2), (150, 150, 150), 1)
                    cv2.putText(frame, f"Detect: {name}", (x1, y1 - 10), 
                                cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 255, 255), 1)

            # ب. استقبال الأوامر من واجهة VB.NET (الماوس + الأزرار)
            try:
                message = self.rep.recv_string(flags=zmq.NOBLOCK)
                action, payload = message.split('|')
                
                if action == "LOCK_ON_TARGET":
                    params = json.loads(payload)
                    # تحويل إحداثيات الماوس من نسبة مئوية إلى بكسلات حقيقية
                    tx = int(params['x'] * w)
                    ty = int(params['y'] * h)
                    
                    # بدء التتبع (Lock-on) حول النقطة المحددة
                    self.target_box = (tx-50, ty-50, 100, 100)
                    self.tracker = cv2.TrackerKCF_create()
                    self.tracker.init(frame, self.target_box)
                    self.is_tracking = True
                    self.current_action = "FOLLOW"
                    print(f"Manual Lock-on at: {tx}, {ty}")
                
                # ترجمة ضغطات الأزرار إلى حالات تكتيكية
                elif action == "TOP_DOWN_PRESSURE":
                    self.current_action = "TOP_DOWN"
                elif action == "LATERAL_NUDGE":
                    self.current_action = "LATERAL"
                elif action == "VISUAL_BLINDING":
                    self.current_action = "BLIND"
                elif action == "ESCORT":
                    self.current_action = "ESCORT"

                self.rep.send_string("ACK")
            except zmq.Again:
                pass

            # ج. معالجة الإطباق (Tracking) وتحديث التوأم الرقمي
            if self.is_tracking:
                success, box = self.tracker.update(frame)
                if success:
                    bx, by, bw, bh = [int(v) for v in box]
                    # رسم مربع الإطباق الأخضر
                    cv2.rectangle(frame, (bx, by), (bx+bw, by+bh), (0, 255, 0), 3)
                    cv2.putText(frame, f"LOCKED: {self.current_action}", (bx, by-15), 
                                cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 2)

                    # 1. تحديث إحداثيات المهاجم (المكعب الأحمر)
                    self.tgt_x = ((bx + bw/2) / w * 10) - 5
                    self.tgt_y = ((by + bh/2) / h * 10) - 5
                    
                    # 2. تحديد الهدف المطلوب للمعترض (المكعب الأخضر) بناءً على التكتيك
                    target_int_x, target_int_y, target_int_z = self.tgt_x, self.tgt_y, 5.0
                    
                    if self.current_action == "TOP_DOWN":
                        target_int_z = 8.0  # الارتفاع فوق الهدف لخلخلة الهواء
                    elif self.current_action == "LATERAL":
                        target_int_x = self.tgt_x + 1.8 # البقاء بجانبه لدفعه
                    elif self.current_action == "BLIND":
                        # التطابق التام لحجب الرؤية
                        target_int_x, target_int_z = self.tgt_x, 5.0 
                    elif self.current_action == "ESCORT":
                        target_int_x = self.tgt_x + 2.5 # مسافة مراقبة

                    # 3. تحريك المعترض بسلاسة (Smoothing)
                    self.int_x += (target_int_x - self.int_x) * 0.15
                    self.int_y += (target_int_y - self.int_y) * 0.15
                    self.int_z += (target_int_z - self.int_z) * 0.15
                else:
                    self.is_tracking = False
                    self.current_action = "IDLE"

            # د. إرسال بيانات القياس (Telemetry) إلى VB.NET
            telemetry = {
                "IntX": self.int_x, "IntY": self.int_y, "IntZ": self.int_z,
                "TgtX": self.tgt_x, "TgtY": self.tgt_y, "TgtZ": self.tgt_z,
                "Distance": float(abs(self.int_x - self.tgt_x)),
                "Suggestion": f"SYSTEM: {self.current_action}" if self.is_tracking else "STATUS: AI SCANNING..."
            }
            self.pub.send_string(json.dumps(telemetry))
            
            # عرض نافذة الكاميرا
            cv2.imshow("AUBR AI Tactical Vision", frame)
            if cv2.waitKey(1) & 0xFF == ord('q'): break

        self.cap.release()
        cv2.destroyAllWindows()

if __name__ == "__main__":
    AUBREngine().run()



