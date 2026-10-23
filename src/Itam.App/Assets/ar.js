// Arabic under the English on the employee pages (owner, 23 October 2026: "Both on screen"): My IT Assets, device requests,
// gate passes, ink requests, the sign-in page and decisions from email. Every title, label, button, column, choice and
// explanation whose English is in the list below gets its Arabic line; names, numbers and other data are never changed.
// Texts still without Arabic are kept in window.itamArMissing (for review in the demo).

const AR={
  // Frame and menu
  'IT Assets':'الأصول التقنية','Asset management':'إدارة الأصول','Workspace':'مساحة العمل','Self-service':'الخدمة الذاتية','Alerts':'التنبيهات','Dashboard':'لوحة المتابعة',
  'My IT Assets':'أصولي التقنية','Assets':'الأصول','Handover':'التسليم والاستلام','Tasks':'المهام','Ink':'الأحبار','Maintenance':'الصيانة','Requests':'الطلبات',
  'Movements':'التنقلات','Purchase orders':'أوامر الشراء','Invoices':'الفواتير','Suppliers':'الموردون','Reports':'التقارير','My Backup':'البديل أثناء إجازتي',
  'End of life':'نهاية العمر','Leavers':'الموظفون المغادرون','Loss cases':'حالات الفقد','Disposals':'الإتلاف','Sales':'المبيعات','AD comparison':'مقارنة الدليل النشط',
  'Licenses':'التراخيص','Administration':'الإدارة','Email outbox':'صندوق البريد الصادر','Administration Center':'مركز الإدارة','Sign out':'تسجيل الخروج',
  'Employee':'موظف','IT Support Specialist':'أخصائي دعم فني','IT Helpdesk Team Leader':'قائد فريق مكتب المساعدة','System Administrator':'مدير الأنظمة',
  'Voice Administrator':'مدير الأنظمة الصوتية','IT Assets Officer':'مسؤول الأصول التقنية','IT Manager':'مدير تقنية المعلومات','Ink Officer':'مسؤول الأحبار',
  'Search this list':'ابحث في هذه القائمة','In all columns':'في كل الأعمدة','No row matches the search.':'لا توجد نتيجة مطابقة للبحث.','Nothing here yet.':'لا يوجد شيء بعد.',
  'Clear':'مسح','Close':'إغلاق','Save':'حفظ','Cancel':'إلغاء','Confirm':'تأكيد','Open':'فتح','Remove':'إزالة','Send':'إرسال','Record':'تسجيل','Optional':'اختياري',
  'Choose':'اختر','Reason (kept in the audit log)':'السبب (يُحفظ في سجل التدقيق)','Type a name or account':'اكتب الاسم أو اسم المستخدم',
  // Sign-in
  'Sign in':'تسجيل الدخول','Username':'اسم المستخدم','Password':'كلمة المرور','Show password':'إظهار كلمة المرور',
  'Use the same username and password you use to sign in to your computer.':'استخدم اسم المستخدم وكلمة المرور نفسيهما اللذين تدخل بهما إلى جهازك.',
  'Forgot your password or your account is locked? Contact the IT Helpdesk.':'نسيت كلمة المرور أو حسابك مقفل؟ تواصل مع مكتب المساعدة التقنية.',
  'Type your username and password.':'اكتب اسم المستخدم وكلمة المرور.','The username or password is wrong.':'اسم المستخدم أو كلمة المرور غير صحيحة.',
  'Signing in…':'جارٍ تسجيل الدخول…','The site did not answer. Check your connection and try again.':'لم يستجب الموقع. تحقق من الاتصال وحاول مرة أخرى.',
  'The site did not answer. Reload the page.':'لم يستجب الموقع. أعد تحميل الصفحة.',
  'Too many wrong passwords. Wait 15 minutes and try again, or ask IT.':'محاولات خاطئة كثيرة. انتظر 15 دقيقة ثم حاول مرة أخرى، أو تواصل مع تقنية المعلومات.',
  // Employee details from AD
  'Employee (Active Directory)':'بيانات الموظف (الدليل النشط)','Active Directory details':'بيانات الدليل النشط','Name':'الاسم','Status':'الحالة','Enabled':'مفعّل',
  'Disabled in AD':'معطّل في الدليل النشط','Job description':'المسمى الوظيفي','Department':'الإدارة','Company':'الشركة','Branch (office)':'الفرع (المكتب)','Region':'المنطقة',
  'HR code':'الرقم الوظيفي','SAP ID':'رقم SAP','Mobile':'الجوال','IP phone':'الهاتف الداخلي','Email':'البريد الإلكتروني','Direct manager':'المدير المباشر',
  // My IT Assets
  'The IT assets you have, your delivery and receipt notes, your requests, and what waits for your decision. Managers also see their team.':'الأصول التقنية التي بعهدتك، ومحاضر التسليم والاستلام، وطلباتك، وما ينتظر قرارك. ويرى المديرون فريقهم أيضاً.',
  'Ask for a device':'طلب جهاز','Request a gate pass':'طلب تصريح خروج','Ask for ink':'طلب حبر','My assets':'أصولي','My documents':'مستنداتي','My requests':'طلباتي',
  'My gate passes and transfers':'تصاريح الخروج والنقل الخاصة بي','My team':'فريقي','My ink requests':'طلبات الحبر الخاصة بي','Waiting for your decision':'بانتظار قرارك',
  'The IT assets in your custody. Open the Asset No. for its delivery note.':'الأصول التقنية في عهدتك. افتح رقم الأصل لعرض محضر التسليم.',
  'Device requests for you, or that you made for a member of your team. Ink requests are on the Ink page.':'طلبات الأجهزة لك، أو التي قدّمتها لأحد أعضاء فريقك. طلبات الحبر في صفحة الأحبار.',
  'Gate passes and transfers you asked for, carry or receive. Open one to confirm the dispatch (as the sender) or the receipt (as the named receiver).':'تصاريح الخروج والنقل التي طلبتها أو تحملها أو تستلمها. افتح التصريح لتأكيد الإرسال (بصفتك المرسل) أو الاستلام (بصفتك المستلم المحدد).',
  'Delivery and receipt notes, newest first.':'محاضر التسليم والاستلام، الأحدث أولاً.',
  'The assets of each member of your team, their open requests and delivery notes not yet signed: follow up before someone leaves.':'أصول كل عضو في فريقك وطلباته المفتوحة ومحاضر التسليم غير الموقّعة: تابعها قبل مغادرة أي موظف.',
  'As the direct manager, or as the IT Manager for every request (or a backup during leave). A rejection needs a reason.':'بصفتك المدير المباشر، أو مدير تقنية المعلومات لكل الطلبات (أو البديل أثناء الإجازة). الرفض يحتاج إلى سبب.',
  'Asset No.':'رقم الأصل','Type':'النوع','Model':'الطراز','Serial / service tag':'الرقم التسلسلي / رقم الخدمة','Computer name':'اسم الجهاز','Delivered':'تاريخ التسليم',
  'Delivery note':'محضر التسليم','Sign the printed note':'وقّع المحضر المطبوع','Signed':'موقّع','Gate pass':'تصريح خروج','Request':'الطلب','For':'المستفيد','What':'ما المطلوب',
  'Asked':'تاريخ الطلب','Me':'أنا','New device':'جهاز جديد','No.':'الرقم','Purpose':'الغرض','From → to':'من ← إلى','My part':'دوري','Exit':'الخروج','Receiver':'المستلم',
  'Sender':'المرسل','Requested':'مقدَّم','No IT assets in your custody.':'لا توجد أصول تقنية في عهدتك.','No IT assets.':'لا توجد أصول تقنية.',
  'No requests yet. Use Ask for a device.':'لا توجد طلبات بعد. استخدم «طلب جهاز».','No gate passes or transfers.':'لا توجد تصاريح خروج أو نقل.','No documents yet.':'لا توجد مستندات بعد.',
  'Nothing waiting for you.':'لا شيء بانتظارك.','Step':'المرحلة','Cost':'التكلفة','Time left':'الوقت المتبقي','Approve':'موافقة','Reject':'رفض','Request approval':'الموافقة على الطلب',
  'Cost approval':'الموافقة على التكلفة','Overdue':'متأخر','Receipt note':'محضر الاستلام','Date':'التاريخ',
  'How it works':'كيف يعمل',
  // Request statuses and steps (from the server)
  'Waiting manager approval':'بانتظار موافقة المدير','With the IT Assets Officer':'لدى مسؤول الأصول التقنية','From stock: to deliver':'من المخزون: جاهز للتسليم',
  'Pricing in region':'التسعير في المنطقة','Waiting cost approval':'بانتظار الموافقة على التكلفة','Cost approved: to order':'تمت الموافقة على التكلفة: للطلب',
  'Ordered':'تم الطلب','Cash purchase in region':'شراء نقدي في المنطقة','Fulfilled':'مكتمل','Rejected':'مرفوض','Cancelled':'ملغى','Submitted':'مقدَّم','Approved':'تمت الموافقة',
  'From stock':'من المخزون','IT accessory: buy in the region':'ملحق تقني: يُشترى في المنطقة','Time is up: cancelled':'انتهت المهلة: أُلغي',
  // Ask for a device and the request page
  'Request type':'نوع الطلب','Replacement':'استبدال','New':'جديد','Device being replaced':'الجهاز المراد استبداله','What is needed and why':'المطلوب وسببه',
  'For employee (IT only)':'للموظف (لتقنية المعلومات فقط)','Leave empty to ask for yourself.':'اتركه فارغاً لتطلب لنفسك.','Send the request':'إرسال الطلب',
  'Your direct manager approves the request first; then IT handles it.':'يوافق مديرك المباشر على الطلب أولاً، ثم تتولاه تقنية المعلومات.',
  'Technical assessment of the device (if not recorded in IT Assets)':'التقييم الفني للجهاز (إن لم يكن مسجلاً في الأصول التقنية)',
  'IT records the technical assessment when the device is checked; attach it here only if you have it as a file.':'تسجّل تقنية المعلومات التقييم الفني عند فحص الجهاز؛ أرفقه هنا فقط إن كان لديك ملف.',
  'Device request · New':'طلب جهاز · جديد','Device request · Replacement':'طلب جهاز · استبدال','Requests for you, or that you asked for someone.':'طلبات لك، أو طلبتها لشخص آخر.',
  'Request document':'مستند الطلب','Request PDF':'ملف الطلب PDF','Progress':'سير الطلب','Request approved (direct manager or IT Manager)':'تمت الموافقة على الطلب (المدير المباشر أو مدير تقنية المعلومات)',
  'Cost approved (direct manager or IT Manager)':'تمت الموافقة على التكلفة (المدير المباشر أو مدير تقنية المعلومات)','Prepared by the IT Assets Officer.':'أعدّه مسؤول الأصول التقنية.',
  'Approve the request':'الموافقة على الطلب','Reject the request':'رفض الطلب','Approve the cost':'الموافقة على التكلفة','Cancel the request':'إلغاء الطلب','Reason':'السبب',
  'Reason for the rejection *':'سبب الرفض *','Why it is rejected (sent to the person who asked)':'سبب الرفض (يُرسل إلى مقدم الطلب)',
  'The person who asked is told by email, with your reason.':'يُبلَّغ مقدم الطلب بالبريد الإلكتروني مع سببك.','Request approved':'تمت الموافقة على الطلب',
  'Grand total including VAT':'الإجمالي شامل ضريبة القيمة المضافة','Subtotal':'المجموع قبل الضريبة','Item':'البند','Quantity':'الكمية','Qty':'الكمية','Line total':'إجمالي البند',
  'Supplier':'المورد','Quotation (required)':'عرض السعر (مطلوب)','Quotation or price photo (optional)':'عرض السعر أو صورة السعر (اختياري)','Specification':'المواصفات',
  'Not your decision':'ليس قرارك','Already decided':'تم اتخاذ القرار مسبقاً','Waiting for your approval':'بانتظار موافقتك','Waiting':'قيد الانتظار',
  'The delivery note of the device given to the employee for this request.':'محضر تسليم الجهاز الذي سُلّم للموظف لهذا الطلب.','Request + delivery note':'الطلب + محضر التسليم',
  // Gate pass
  'New gate pass':'تصريح خروج جديد','Movement':'التنقل','Purpose details':'تفاصيل الغرض','From branch':'من الفرع','Destination branch':'الفرع المستلم','Destination':'الوجهة',
  'Pass type':'نوع التصريح','Exit date':'تاريخ الخروج','Expected receipt / return':'تاريخ الاستلام / العودة المتوقع','Named receiving employee':'الموظف المستلم','Named sender':'المرسل',
  'Carried by':'يحمله','Carrier name':'اسم الحامل','Carrier mobile':'جوال الحامل','Find assets':'ابحث عن الأصول','Scan or paste several':'امسح أو الصق عدة أرقام',
  'Select all shown':'تحديد كل المعروض','Clear the selection':'مسح التحديد','Add these assets':'إضافة هذه الأصول','Assets on this movement':'الأصول في هذا التنقل',
  'No asset chosen yet.':'لم يتم اختيار أي أصل بعد.','No asset found that you can move.':'لا يوجد أصل يمكنك نقله.','Send for approval':'إرسال للموافقة',
  'Asset No., serial or service tag':'رقم الأصل أو الرقم التسلسلي أو رقم الخدمة','One Asset No. or serial per line':'رقم أصل أو رقم تسلسلي واحد في كل سطر',
  'One Asset No. or serial per line; a barcode scanner adds a new line after each label.':'رقم أصل أو رقم تسلسلي واحد في كل سطر؛ قارئ الباركود يضيف سطراً جديداً بعد كل ملصق.',
  'Your own assets, or (IT) the assets of your regions.':'أصولك، أو (لتقنية المعلومات) أصول مناطقك.','Required when the purpose is Other.':'مطلوب عندما يكون الغرض «أخرى».',
  'For example the customer site or the supplier.':'مثلاً موقع العميل أو المورد.','The pass is valid for dispatch for 3 days from this date.':'التصريح صالح للخروج 3 أيام من هذا التاريخ.',
  'Leave empty for yourself (or the employee whose assets they are).':'اتركه فارغاً إن كنت أنت (أو الموظف صاحب الأصول).',
  'Take assets out of a building: to another branch (transfer) or for another purpose. The IT Helpdesk Team Leader, System Administrator or IT Manager approves; print the pass for Security.':'إخراج أصول من المبنى: إلى فرع آخر (نقل) أو لغرض آخر. يوافق قائد فريق مكتب المساعدة أو مدير الأنظمة أو مدير تقنية المعلومات؛ ثم اطبع التصريح للأمن.',
  'Move to another branch':'النقل إلى فرع آخر','Customer or company site':'موقع عميل أو موقع للشركة','Exhibition or event':'معرض أو فعالية','Work from home':'العمل من المنزل',
  'Repair outside (maintenance order)':'إصلاح خارجي (أمر صيانة)','Return to the supplier':'إعادة إلى المورد','Other':'أخرى',
  'Returnable (comes back)':'مُرتجع (يعود)','Non-returnable (IT only)':'غير مُرتجع (لتقنية المعلومات فقط)','Returnable':'مُرتجع','Non-returnable':'غير مُرتجع',
  'The employee (sender)':'الموظف (المرسل)','Another person':'شخص آخر','The employee':'الموظف','Employee responsible':'الموظف المسؤول','Exit (dispatch) date':'تاريخ الخروج (الإرسال)',
  'Valid until':'صالح حتى','Expected return':'تاريخ العودة المتوقع','Expected receipt':'تاريخ الاستلام المتوقع','From':'من','To':'إلى','Security checks the printed pass against the labels.':'يطابق الأمن التصريح المطبوع مع ملصقات الأصول.',
  'Had it':'كان بعهدة','State':'الحالة','Received':'الاستلام','Resolution':'المعالجة','Waiting dispatch':'بانتظار الإرسال','Dispatched':'تم الإرسال','Missing':'مفقود','Damaged':'تالف',
  'Incorrect':'غير صحيح','Pending':'قيد الانتظار','Print the gate pass':'طباعة تصريح الخروج','Record the dispatch':'تسجيل الإرسال','Confirm the receipt':'تأكيد الاستلام',
  'Confirm the return':'تأكيد العودة','The assets are back':'عادت الأصول','The assets left':'خرجت الأصول','Approved: print the gate pass for Security.':'تمت الموافقة: اطبع تصريح الخروج للأمن.',
  'Partially dispatched':'أُرسل جزئياً','In transit':'في الطريق','Out of the building':'خارج المبنى','Partially received':'استُلم جزئياً','Discrepancy pending':'اختلاف قيد المعالجة',
  'Awaiting the signed delivery note':'بانتظار محضر التسليم الموقّع','Closed with exceptions':'أُغلق مع استثناءات','Completed':'مكتمل','Expired':'منتهي الصلاحية','Closed':'مغلق',
  'Received in good condition':'استُلم بحالة جيدة','Not yet (still on the way)':'ليس بعد (ما زال في الطريق)','Incorrect (another asset)':'غير صحيح (أصل آخر)',
  'Valid until (dispatch)':'صالح حتى (للإرسال)','You can shorten the dispatch validity.':'يمكنك تقصير مدة صلاحية الإرسال.','Dispatch recorded.':'تم تسجيل الإرسال.','Receipt recorded.':'تم تسجيل الاستلام.',
  'Return confirmed: the pass is closed.':'تم تأكيد العودة: أُغلق التصريح.','Created.':'تم الإنشاء.','Exit pass':'تصريح خروج','Branch transfer':'نقل بين الفروع',
  'Check each label against the pass. Missing, damaged or incorrect assets go to an approver to resolve.':'طابق كل ملصق مع التصريح. الأصول المفقودة أو التالفة أو غير الصحيحة تُحال إلى المعتمد لمعالجتها.',
  'The named receiver confirms each asset. When the receiver is not the person who had it, the old custody closes and the asset is In Store at the destination: IT there delivers it to the receiver (delivery note signed on paper).':'يؤكد المستلم المحدد كل أصل. إذا لم يكن المستلم هو صاحب العهدة، تُغلق العهدة السابقة ويصبح الأصل في المخزن في الوجهة، وتسلّمه تقنية المعلومات هناك للمستلم (بمحضر تسليم موقّع ورقياً).',
  // Ink
  'Ask for toner for your printer. The Ink Officer of your region sends your request to the approving manager; once approved, the Ink Officer delivers it from the shelf or orders it from the supplier. No paper to sign: the request and its approval are kept in IT Assets.':'اطلب حبراً لطابعتك. يرسل مسؤول الأحبار في منطقتك طلبك إلى المدير المعتمد؛ وبعد الموافقة يسلّمه مسؤول الأحبار من المخزون أو يطلبه من المورد. لا توجد أوراق للتوقيع: الطلب وموافقته محفوظان في الأصول التقنية.',
  'Ask for ink: find your printer':'طلب حبر: ابحث عن طابعتك','Ask for ink: printer not in IT Assets':'طلب حبر: الطابعة غير مسجلة في الأصول التقنية','Printer':'الطابعة','Toner':'الحبر',
  'Toner code':'رمز الحبر','Printer location':'موقع الطابعة','Printer model':'طراز الطابعة','Printer Asset No.':'رقم أصل الطابعة','Serial number':'الرقم التسلسلي',
  'Serial number of the printer':'الرقم التسلسلي للطابعة','Printer model (as written on the printer)':'طراز الطابعة (كما هو مكتوب عليها)','Manufacturer':'الشركة المصنّعة',
  'None of these: my printer is not in the list':'لا شيء مما سبق: طابعتي غير موجودة في القائمة','Approving manager':'المدير المعتمد','Note':'ملاحظة',
  'A senior manager (for example the Head of Department). Type part of the name, then choose.':'مدير أعلى (مثلاً رئيس الإدارة). اكتب جزءاً من الاسم ثم اختر.',
  'For example which colour is empty.':'مثلاً أي لون نفد.','The code printed on the cartridge (for example W2210A).':'الرمز المطبوع على الخرطوشة (مثلاً W2210A).',
  'Filled from the printer; change it if needed (for example which colour).':'يُملأ من بيانات الطابعة؛ غيّره عند الحاجة (مثلاً اللون).',
  'As written on the printer, without the manufacturer (for example HL-L2350DW).':'كما هو مكتوب على الطابعة، بدون اسم الشركة (مثلاً HL-L2350DW).',
  'Not found in IT Assets. Your region and office are taken from Active Directory.':'غير موجودة في الأصول التقنية. تؤخذ منطقتك ومكتبك من الدليل النشط.',
  'No ink requests yet. Use Ask for ink.':'لا توجد طلبات حبر بعد. استخدم «طلب حبر».','Document':'المستند','Waiting for the Ink Officer':'بانتظار مسؤول الأحبار',
  'Ordered from supplier':'مطلوب من المورد','For example Reception or 2nd floor.':'مثلاً الاستقبال أو الدور الثاني.',
  // Decision from email
  'Decision from email':'قرار من البريد الإلكتروني','Decision':'القرار','Your approval is recorded; the next step is told by email.':'تم تسجيل موافقتك؛ وسيُبلَّغ بالخطوة التالية بالبريد الإلكتروني.',
  'Your approval is recorded; the next step is told by email. You can close this page.':'تم تسجيل موافقتك؛ وسيُبلَّغ بالخطوة التالية بالبريد الإلكتروني. يمكنك إغلاق هذه الصفحة.',
  'This record is no longer waiting for this decision, so the email link does nothing more.':'هذا السجل لم يعد بانتظار هذا القرار، لذا لن يفعل رابط البريد شيئاً.',
  'Approve instead':'الموافقة بدلاً من ذلك','Not approved':'غير موافق عليه',
  // Asset types and conditions
  'Computer':'حاسب آلي','IP Phone':'هاتف شبكي','Monitor':'شاشة','Scanner':'ماسح ضوئي','Software':'برنامج','Printer Cartridge':'خرطوشة حبر','In Store':'في المخزن','In Use':'قيد الاستخدام',
  'Working':'يعمل','Faulty':'معطّل','Used':'مستعمل',
  // Explanations (how it works)
  'Your devices: the IT assets in your custody. When IT gives you a device, sign the printed delivery note and give it back to IT.':'أجهزتك: الأصول التقنية في عهدتك. عندما تسلّمك تقنية المعلومات جهازاً، وقّع محضر التسليم المطبوع وأعده إلى تقنية المعلومات.',
  'Ask for a device: a new device or a replacement. Your direct manager approves, then IT gives you one from stock or buys it.':'طلب جهاز: جهاز جديد أو بديل. يوافق مديرك المباشر، ثم تسلّمك تقنية المعلومات جهازاً من المخزون أو تشتريه.',
  'Request a gate pass: to take your device out of the building, or to move it to another branch. An IT approver agrees, then you show the printed pass to Security.':'طلب تصريح خروج: لإخراج جهازك من المبنى أو نقله إلى فرع آخر. يوافق أحد معتمدي تقنية المعلومات، ثم تُظهر التصريح المطبوع للأمن.',
  'Ask for ink: toner for your printer. A senior manager approves, then the Ink Officer of your region delivers it.':'طلب حبر: حبر لطابعتك. يوافق مدير أعلى، ثم يسلّمه مسؤول الأحبار في منطقتك.',
  'Leaving the company? Give back every device in your custody to IT and get the receipt note.':'ستغادر الشركة؟ أعد كل جهاز في عهدتك إلى تقنية المعلومات واستلم محضر الاستلام.',
  'Questions? Contact the IT Helpdesk.':'لديك سؤال؟ تواصل مع مكتب المساعدة التقنية.',
  'Choose New for a device you do not have yet, or Replacement for a device you have that is broken or too old.':'اختر «جديد» لجهاز ليس لديك، أو «استبدال» لجهاز لديك معطّل أو قديم.',
  'Write what you need and why (for example: a laptop for site visits). Your direct manager gets an email to approve or reject.':'اكتب ما تحتاجه وسببه (مثلاً: حاسب محمول للزيارات الميدانية). يصل مديرك المباشر بريد للموافقة أو الرفض.',
  'After approval, the IT Assets Officer gives a device from stock, or prices it and asks your manager to approve the cost.':'بعد الموافقة، يسلّم مسؤول الأصول التقنية جهازاً من المخزون، أو يسعّره ويطلب من مديرك الموافقة على التكلفة.',
  'You get an email at every step. A request not decided in 3 days is cancelled automatically.':'يصلك بريد في كل مرحلة. الطلب الذي لا يُتخذ فيه قرار خلال 3 أيام يُلغى تلقائياً.',
  'Choose the purpose and the asset (your own devices are listed below).':'اختر الغرض والأصل (أجهزتك معروضة أدناه).',
  'Moving to another branch: name the employee who receives it there. For other purposes: write the destination and when it comes back.':'النقل إلى فرع آخر: حدّد الموظف الذي سيستلمه هناك. للأغراض الأخرى: اكتب الوجهة وموعد العودة.',
  'The IT Helpdesk Team Leader, System Administrator or IT Manager approves. Then print the pass; Security checks it against the labels on the devices.':'يوافق قائد فريق مكتب المساعدة أو مدير الأنظمة أو مدير تقنية المعلومات. ثم اطبع التصريح؛ ويطابقه الأمن مع ملصقات الأجهزة.',
  'The pass can be used for 3 days from the exit date. When the devices are back, confirm the return on the pass.':'يمكن استخدام التصريح خلال 3 أيام من تاريخ الخروج. عند عودة الأجهزة أكّد العودة على التصريح.',
  'Find your printer by its Asset No. (on the label), serial number or toner code, or choose it from the list.':'ابحث عن طابعتك برقم الأصل (على الملصق) أو الرقم التسلسلي أو رمز الحبر، أو اخترها من القائمة.',
  'Choose the senior manager who approves (for example your Head of Department). They get an email.':'اختر المدير الأعلى الذي يوافق (مثلاً رئيس إدارتك). يصله بريد إلكتروني.',
  'After approval, the Ink Officer of your region delivers the toner from the shelf or orders it from the supplier. Follow it under My ink requests.':'بعد الموافقة، يسلّم مسؤول الأحبار في منطقتك الحبر من المخزون أو يطلبه من المورد. تابعه في «طلبات الحبر الخاصة بي».',
  // IT parts of the same pages (cost table, purchase, ink orders, warehouse transfers)
  'Find':'بحث','By':'بواسطة','All regions':'كل المناطق','on the Ink page.':'في صفحة الأحبار.','Employee (no IT role)':'موظف (بدون دور تقني)','Printer Asset No., serial number or toner code':'رقم أصل الطابعة أو الرقم التسلسلي أو رمز الحبر',
  'The Asset No. is on the IT label of the printer (for example PNT-4811). No label? Type the serial number (S/N) from the sticker on the printer.':'رقم الأصل موجود على ملصق تقنية المعلومات على الطابعة (مثلاً PNT-4811). لا يوجد ملصق؟ اكتب الرقم التسلسلي (S/N) من الملصق الموجود على الطابعة.',
  'On the sticker at the back or bottom of the printer (S/N or Serial No.).':'على الملصق في الجهة الخلفية أو السفلية للطابعة (S/N أو Serial No.).',
  'No cost table yet.':'لا يوجد جدول تكلفة بعد.','Device In Store':'جهاز في المخزن','Reserved on this request until it is delivered.':'محجوز لهذا الطلب حتى يُسلَّم.',
  'The PO the device is ordered on; receive the device on the PO as usual, then deliver it to the employee.':'أمر الشراء الذي طُلب عليه الجهاز؛ استلم الجهاز على أمر الشراء كالمعتاد، ثم سلّمه للموظف.',
  'Purchase date':'تاريخ الشراء','Amount paid including VAT (SAR)':'المبلغ المدفوع شامل الضريبة (ريال)','Choose the source':'اختر المصدر','Record the purchase':'تسجيل الشراء',
  'Link the delivery note':'ربط محضر التسليم','Send the cost to the manager':'إرسال التكلفة إلى المدير','Add a line':'إضافة بند','Sent to Accounting':'أُرسل إلى المحاسبة',
  'From stock (a device In Store)':'من المخزون (جهاز في المخزن)','New device (buy with a PO)':'جهاز جديد (شراء بأمر شراء)','Dispatch the ticked assets':'إرسال الأصول المحددة',
  'Untick the assets that stay at the source for now.':'ألغِ تحديد الأصول التي ستبقى في المصدر حالياً.','Required when damaged, incorrect or missing.':'مطلوب عند التلف أو الخطأ أو الفقد.',
  'Investigation findings':'نتائج التحقق','That asset cannot go on a gate pass now (it may already be on one).':'لا يمكن إدراج هذا الأصل في تصريح خروج الآن (قد يكون مدرجاً في تصريح آخر).',
  'Arrived later (received at the destination)':'وصل لاحقاً (استُلم في الوجهة)','Accepted at the destination (damaged or incorrect)':'قُبل في الوجهة (تالف أو غير صحيح)',
  'Returned to the source':'أُعيد إلى المصدر','Never dispatched (still at the source)':'لم يُرسل (ما زال في المصدر)','Confirmed lost':'تأكد فقده','Kept at':'مكان الحفظ',
  'New model name':'اسم الطراز الجديد','Register and print the label':'تسجيل وطباعة الملصق','No orders yet.':'لا توجد طلبيات بعد.','Create the order':'إنشاء الطلبية',
  'Delivery date':'تاريخ التسليم','Invoice line':'بند الفاتورة','Put the ticked requests in a supplier order':'ضع الطلبات المحددة في طلبية للمورد','Link printer':'ربط الطابعة',
  'Register printer':'تسجيل الطابعة','Requests to handle (Ink Officer)':'طلبات للمعالجة (مسؤول الأحبار)','Supplier orders':'طلبيات الموردين','To send for approval':'للإرسال للموافقة',
  'Waiting approval':'بانتظار الموافقة','To order or deliver':'للطلب أو التسليم','Not in the list':'غير موجود في القائمة','The IT shelf':'مخزون تقنية المعلومات','A supplier invoice':'فاتورة مورد',
  'Approving…':'جارٍ تسجيل الموافقة…','Approver / order':'المعتمد / الطلبية','Change approver':'تغيير المعتمد','Change the cost table':'تعديل جدول التكلفة','Cost center':'مركز التكلفة',
  'Cost table sent to the direct manager for approval.':'أُرسل جدول التكلفة إلى المدير المباشر للموافقة.','Decided by':'صاحب القرار','Deliver':'تسليم',
  'Deliver the device to the employee from its asset page first.':'سلّم الجهاز للموظف من صفحة الأصل أولاً.','Email draft':'مسودة بريد','Evidence':'الدليل','Invoice':'الفاتورة',
  'Link':'ربط','Maintenance order':'أمر صيانة','Order':'الطلبية','Order PDF':'الطلبية PDF','Priced':'مسعّر','Prepare the cost table':'إعداد جدول التكلفة',
  'Print the order, or open it as an email to the supplier (nothing is sent automatically).':'اطبع الطلبية، أو افتحها كبريد إلى المورد (لا يُرسل شيء تلقائياً).',
  'Purchase recorded: the request is fulfilled.':'تم تسجيل الشراء: اكتمل الطلب.','Shelf':'المخزون','Shop':'محل','Source':'المصدر','Stock':'المخزون',
  'Unit cost (SAR, before VAT)':'سعر الوحدة (ريال، قبل الضريبة)','Warehouse':'المستودع','Where it is bought in the region.':'مكان الشراء في المنطقة.','Attach the evidence.':'أرفق الدليل.',
  'Attach the receipt.':'أرفق الإيصال.','Attached file':'الملف المرفق','Choose the manufacturer.':'اختر الشركة المصنّعة.','Choose the model.':'اختر الطراز.','Choose the person from the list.':'اختر الشخص من القائمة.',
  'Scan or paste at least one Asset No. or serial.':'امسح أو الصق رقم أصل أو رقماً تسلسلياً واحداً على الأقل.','Choose the source warehouse first.':'اختر المستودع المصدر أولاً.',
  'Accepted at the destination':'قُبل في الوجهة','Arrived later':'وصل لاحقاً','Never dispatched':'لم يُرسل','Not recorded yet':'لم يُسجل بعد','Not registered':'غير مسجل','Accessory':'ملحق',
  'When':'الوقت','Warehouse transfer':'نقل بين المستودعات','New warehouse transfer':'نقل جديد بين المستودعات','Source IT warehouse':'المستودع المصدر','Destination IT warehouse':'المستودع المستلم',
  'Destination IT receiver':'المستلم من تقنية المعلومات في الوجهة','Expected arrival':'تاريخ الوصول المتوقع','Planned dispatch':'موعد الإرسال المخطط','Submit and reserve the stock':'إرسال وحجز المخزون',
  'Only assets In Store at the source warehouse.':'الأصول الموجودة في المستودع المصدر فقط.','Named receiver':'المستلم المحدد','Receipt':'الاستلام','Returned':'أُعيد',
  'Move IT stock between IT warehouses: no approval; the stock is reserved and a pass is issued for Security.':'نقل مخزون تقنية المعلومات بين المستودعات: بدون موافقة؛ يُحجز المخزون ويصدر تصريح للأمن.',
  'Dispatch some or all assets; the destination IT receiver confirms each one. Missing, damaged or incorrect assets stay open until an approver (Team Leader, System Administrator or IT Manager) resolves them.':'أرسل بعض الأصول أو كلها؛ ويؤكد مستلم تقنية المعلومات في الوجهة كل أصل. تبقى الأصول المفقودة أو التالفة أو غير الصحيحة مفتوحة حتى يعالجها معتمد (قائد الفريق أو مدير الأنظمة أو مدير تقنية المعلومات).',
};

// Texts with a number or a name inside.
const PATTERNS=[
  [/^(\d+) delivery notes? (?:is|are) waiting for your signature: sign the printed note and give it to IT\.$/,m=>`${m[1]} محضر تسليم بانتظار توقيعك: وقّع المحضر المطبوع وأعطه لتقنية المعلومات.`],
  [/^(\d+) assets? on this (pass|transfer)$/,m=>`${m[1]} أصل في هذا ${m[2]==='pass'?'التصريح':'النقل'}`],
  [/^Read from Active Directory, last updated (.+)\.$/,m=>`مقروءة من الدليل النشط، آخر تحديث ${m[1]}.`],
  [/^(\d+) ink requests? waiting for your approval$/,m=>`${m[1]} طلب حبر بانتظار موافقتك`],
  [/^(.+) \(dispatch only\)$/,m=>`${m[1]} (للإرسال فقط)`],
  [/^(\d+) days? left$/,m=>`متبقٍ ${m[1]} يوم`],
  [/^Approve (\S+)$/,m=>`الموافقة على ${m[1]}`],[/^Reject (\S+)$/,m=>`رفض ${m[1]}`],
  [/^Delivered (\S+) · IT shelf$/,m=>`سُلّم ${m[1]} · من مخزون تقنية المعلومات`],[/^Delivered (\S+)$/,m=>`سُلّم ${m[1]}`],
  [/^(.+) sent to the direct manager for approval\.$/,m=>`أُرسل ${m[1]} إلى المدير المباشر للموافقة.`],
  [/^Source: (.+?)(?: · assigned to (.+))?$/,m=>`المصدر: ${arabic(m[1])||m[1]}${m[2]?' · مُسند إلى '+m[2]:''}`],[/^Shop: (.+)$/,m=>`المحل: ${m[1]}`],
  [/^VAT (\d+)%$/,m=>`ضريبة القيمة المضافة ${m[1]}%`],[/^Replacement of (\S+)$/,m=>`استبدال ${m[1]}`],
  // The table search and sort (itam.js).
  [/^In (.+)$/,m=>`في ${arabic(m[1])||m[1]}`],[/^(\d+) rows?$/,m=>`${m[1]} صف`],[/^(\d+) of (\d+)$/,m=>`${m[1]} من ${m[2]}`],
  [/^Sort by (.+)$/,m=>`ترتيب حسب ${arabic(m[1])||m[1]}`],
];

export function arabic(text){
  const t=(text||'').replace(/\s+/g,' ').trim();if(!t)return null;
  if(Object.hasOwn(AR,t))return AR[t];
  // A required field's label ends with " *".
  const req=t.match(/^(.+?)\s*\*$/);if(req&&Object.hasOwn(AR,req[1]))return AR[req[1]]+' *';
  // A line that continues the one before it (" · Work from home · ...").
  const lead=t.match(/^· (.+)$/);if(lead){const a=arabic(lead[1]);return a?'· '+a:null;}
  // The countdown of a request waiting for a decision (itam.js timeLeft).
  const left=t.match(/^(?:(\d+) days? )?(?:(\d+) h ?)?(?:(\d+) min)? left$/);
  if(left&&(left[1]||left[2]||left[3]))return 'متبقٍ '+[left[1]&&`${left[1]} يوم`,left[2]&&`${left[2]} ساعة`,left[3]&&`${left[3]} دقيقة`].filter(Boolean).join(' و');
  for(const [rx,fn] of PATTERNS){const m=t.match(rx);if(m)return fn(m);}
  // Parts joined with " · " or ", " (for example "Cost approval · New device", or roles): each known part in Arabic, the
  // rest (names, numbers) kept as they are; nothing when no part is known.
  for(const sep of [' · ',', ']){
    if(!t.includes(sep))continue;
    const parts=t.split(sep).map(p=>p.trim()).filter(Boolean);const ar=parts.map(p=>arabic(p));
    if(ar.some(Boolean))return parts.map((p,i)=>ar[i]||p).join(sep===', '?'، ':' · ');
  }
  return null;
}

const missing=window.itamArMissing=new Set();
const done=new WeakSet();
const SKIP=new Set(['SCRIPT','STYLE','INPUT','TEXTAREA','SELECT','OPTION','TITLE','SVG']);
const INLINE=new Set(['BUTTON','A','B','STRONG','SMALL','EM','TD','DD']);
function inline(p){return INLINE.has(p.tagName)||p.classList.contains('tag')||p.classList.contains('x-count')||(p.tagName==='SPAN'&&!p.classList.contains('x-l')&&!p.classList.contains('x-help'));}

function textNode(n){
  if(done.has(n))return;const p=n.parentElement;if(!p||SKIP.has(p.tagName)||p.closest('.ar,.mono,svg,[data-no-ar]'))return;
  const t=n.nodeValue.replace(/\s+/g,' ').trim();if(!/[A-Za-z]{2}/.test(t))return;
  done.add(n);
  const a=arabic(t);if(!a){if(t.length<400)missing.add(t);return;}
  if(n.nextSibling?.nodeType===1&&n.nextSibling.classList?.contains('ar'))return;
  const s=document.createElement('span');s.className='ar'+(inline(p)?' ar-i':'');s.lang='ar';s.dir='rtl';s.textContent=a;n.after(s);
}
function option(o){if(o.dataset.ar)return;const a=arabic(o.textContent);o.dataset.ar='1';if(a)o.textContent=`${o.textContent} — ${a}`;else if(/[A-Za-z]{2}/.test(o.textContent))missing.add(o.textContent.trim());}
function placeholder(i){if(i.dataset.arPh||!i.placeholder)return;i.dataset.arPh='1';const a=arabic(i.placeholder);if(a)i.placeholder=`${i.placeholder} — ${a}`;}

function scan(root){
  if(root.nodeType===3){textNode(root);return;}
  if(root.nodeType!==1||root.closest?.('.ar'))return;
  const w=document.createTreeWalker(root,NodeFilter.SHOW_TEXT);let n;const list=[];while((n=w.nextNode()))list.push(n);list.forEach(textNode);
  root.querySelectorAll?.('option').forEach(option);if(root.tagName==='OPTION')option(root);
  root.querySelectorAll?.('input[placeholder],textarea[placeholder]').forEach(placeholder);
}

/** Turns on Arabic under the English for this page, now and for everything the page adds later (dialogs, tables). */
export function bilingual(){
  document.documentElement.classList.add('x-bi');
  scan(document.body);
  new MutationObserver(list=>{for(const r of list){if(r.type==='characterData'){done.delete(r.target);const s=r.target.nextSibling;if(s?.classList?.contains('ar'))s.remove();textNode(r.target);}else for(const n of r.addedNodes)scan(n);}})
    .observe(document.body,{childList:true,subtree:true,characterData:true});
}

/** A "How it works" box: numbered steps (each English with its Arabic line). */
export function steps(title,list){
  const box=document.createElement('section');box.className='card x-howto';const h=document.createElement('h2');h.textContent=title||'How it works';box.append(h);
  const ol=document.createElement('ol');for(const t of list){const li=document.createElement('li');li.textContent=t;ol.append(li);}box.append(ol);return box;
}

/** The same steps at the top of the open form dialog (call right after edit()). */
export function dialogSteps(list){const b=document.getElementById('dialog-body');if(!b)return;const s=steps('How it works',list);s.classList.add('wide','x-howto-in');b.prepend(s);}
