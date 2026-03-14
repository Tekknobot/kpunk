#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>

#if __has_include(<UniformTypeIdentifiers/UniformTypeIdentifiers.h>)
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#endif

extern "C" void UnitySendMessage(const char *obj, const char *method, const char *msg);
extern UIViewController *UnityGetGLViewController(void);

@interface KMusicAudioPickerDelegate : NSObject<UIDocumentPickerDelegate>
@property (nonatomic, copy) NSString *targetObject;
@property (nonatomic, copy) NSString *successCallback;
@property (nonatomic, copy) NSString *cancelCallback;
@end

@implementation KMusicAudioPickerDelegate

- (NSString *)copyPickedFileToImports:(NSURL *)sourceURL {
    if (sourceURL == nil) return nil;

    NSFileManager *fm = [NSFileManager defaultManager];
    NSArray<NSURL *> *docDirs = [fm URLsForDirectory:NSDocumentDirectory inDomains:NSUserDomainMask];
    NSURL *documentsURL = docDirs.firstObject;
    if (documentsURL == nil) return nil;

    NSURL *importsURL = [documentsURL URLByAppendingPathComponent:@"ImportedSamples" isDirectory:YES];
    NSError *dirError = nil;
    [fm createDirectoryAtURL:importsURL withIntermediateDirectories:YES attributes:nil error:&dirError];
    if (dirError != nil) return nil;

    NSString *baseName = sourceURL.lastPathComponent ?: @"sample";
    NSURL *destURL = [importsURL URLByAppendingPathComponent:baseName];

    NSString *stem = [baseName stringByDeletingPathExtension];
    NSString *ext = [baseName pathExtension];
    NSInteger suffix = 1;
    while ([fm fileExistsAtPath:destURL.path]) {
        NSString *candidate = ext.length > 0
            ? [NSString stringWithFormat:@"%@_%ld.%@", stem, (long)suffix, ext]
            : [NSString stringWithFormat:@"%@_%ld", stem, (long)suffix];
        destURL = [importsURL URLByAppendingPathComponent:candidate];
        suffix++;
    }

    NSError *copyError = nil;
    [fm copyItemAtURL:sourceURL toURL:destURL error:&copyError];
    if (copyError != nil) return nil;

    return destURL.path;
}

- (void)documentPicker:(UIDocumentPickerViewController *)controller didPickDocumentsAtURLs:(NSArray<NSURL *> *)urls {
    NSURL *pickedURL = urls.firstObject;
    if (pickedURL == nil) {
        UnitySendMessage(self.targetObject.UTF8String, self.cancelCallback.UTF8String, "");
        return;
    }

    BOOL accessed = [pickedURL startAccessingSecurityScopedResource];
    NSString *copiedPath = [self copyPickedFileToImports:pickedURL];
    if (accessed) {
        [pickedURL stopAccessingSecurityScopedResource];
    }

    if (copiedPath != nil) {
        UnitySendMessage(self.targetObject.UTF8String, self.successCallback.UTF8String, copiedPath.UTF8String);
    } else {
        UnitySendMessage(self.targetObject.UTF8String, self.cancelCallback.UTF8String, "");
    }
}

- (void)documentPickerWasCancelled:(UIDocumentPickerViewController *)controller {
    UnitySendMessage(self.targetObject.UTF8String, self.cancelCallback.UTF8String, "");
}

@end

static KMusicAudioPickerDelegate *s_pickerDelegate = nil;

extern "C" void KMusic_OpenAudioDocumentPicker(const char *gameObjectName, const char *successCallback, const char *cancelCallback) {
    dispatch_async(dispatch_get_main_queue(), ^{
        UIViewController *controller = UnityGetGLViewController();
        if (controller == nil) return;

        s_pickerDelegate = [KMusicAudioPickerDelegate new];
        s_pickerDelegate.targetObject = [NSString stringWithUTF8String:(gameObjectName ?: "")];
        s_pickerDelegate.successCallback = [NSString stringWithUTF8String:(successCallback ?: "")];
        s_pickerDelegate.cancelCallback = [NSString stringWithUTF8String:(cancelCallback ?: "")];

        UIDocumentPickerViewController *picker = nil;
#if __has_include(<UniformTypeIdentifiers/UniformTypeIdentifiers.h>)
        if (@available(iOS 14.0, *)) {
            NSArray *types = @[ UTTypeAudio ];
            picker = [[UIDocumentPickerViewController alloc] initForOpeningContentTypes:types asCopy:NO];
        }
#endif
        if (picker == nil) {
            NSArray<NSString *> *types = @[
                @"public.audio",
                @"public.mp3",
                @"com.microsoft.waveform-audio",
                @"public.aac-audio",
                @"org.xiph.ogg-vorbis"
            ];
            picker = [[UIDocumentPickerViewController alloc] initWithDocumentTypes:types inMode:UIDocumentPickerModeImport];
        }

        picker.delegate = s_pickerDelegate;
        picker.modalPresentationStyle = UIModalPresentationFormSheet;
        [controller presentViewController:picker animated:YES completion:nil];
    });
}
