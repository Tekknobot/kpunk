package com.zillatronics.kmusic;

import android.app.Activity;
import android.content.ContentResolver;
import android.database.Cursor;
import android.net.Uri;
import android.os.Build;
import android.provider.MediaStore;
import android.util.Log;
import android.webkit.MimeTypeMap;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.util.ArrayList;

/**
 * MediaStore bridge for Unity.
 *
 * - Query the user's indexed music library via MediaStore.Audio.Media.
 * - Filter to the "Music/" collection folder (best effort):
 *      * API 29+ : MediaStore.MediaColumns.RELATIVE_PATH LIKE 'Music/%'
 *      * <=28    : MediaStore.Audio.Media.DATA LIKE '%/Music/%' (legacy)
 * - Copy a selected content:// URI into the app cache directory so Unity can load it as file://...
 *
 * Unity should handle runtime permissions (READ_MEDIA_AUDIO / READ_EXTERNAL_STORAGE)
 * using UnityEngine.Android.Permission.
 */
public final class MediaStoreBridge {
    private static final String TAG = "KMusicMediaStore";

    private MediaStoreBridge() {}

    /** Returns a JSON string: {"tracks":[{title,artist,album,durationMs,uri,mime,relativePath,dataPath}...]} */
    public static String queryMusicJson(Activity activity, int limit) {
        try {
            if (activity == null) {
                return "{\"tracks\":[],\"error\":\"activity is null\"}";
            }

            ContentResolver cr = activity.getContentResolver();
            Uri collection = MediaStore.Audio.Media.EXTERNAL_CONTENT_URI;

            // Build projection. DATA is deprecated/blocked on modern Android, only ask for it on <=28.
            ArrayList<String> cols = new ArrayList<>();
            cols.add(MediaStore.Audio.Media._ID);
            cols.add(MediaStore.Audio.Media.TITLE);
            cols.add(MediaStore.Audio.Media.ARTIST);
            cols.add(MediaStore.Audio.Media.ALBUM);
            cols.add(MediaStore.Audio.Media.DURATION);
            cols.add(MediaStore.Audio.Media.MIME_TYPE);

            final boolean hasRelativePath = Build.VERSION.SDK_INT >= 29;
            if (hasRelativePath) {
                cols.add(MediaStore.MediaColumns.RELATIVE_PATH); // e.g. "Music/Album/"
            } else {
                cols.add(MediaStore.Audio.Media.DATA);           // legacy absolute path
            }

            String[] projection = cols.toArray(new String[0]);

            // Folder filter (best effort)
            String selection;
            String[] selectionArgs;

            if (hasRelativePath) {
                // RELATIVE_PATH can be "Music/" or "Music/Album/" etc.
                // Some devices/cases vary in case and formatting, so we match a few patterns.
                selection =
                        MediaStore.Audio.Media.IS_MUSIC + "!=0 AND (" +
                        MediaStore.MediaColumns.RELATIVE_PATH + " LIKE ? OR " +   // Music/Album/
                        MediaStore.MediaColumns.RELATIVE_PATH + " LIKE ? OR " +   // MUSIC/Album/
                        MediaStore.MediaColumns.RELATIVE_PATH + " = ? OR " +      // Music/
                        MediaStore.MediaColumns.RELATIVE_PATH + " = ? OR " +      // MUSIC/
                        MediaStore.MediaColumns.RELATIVE_PATH + " LIKE ? OR " +   // contains Music/
                        MediaStore.MediaColumns.RELATIVE_PATH + " LIKE ?" +       // contains MUSIC/
                        ")";
                selectionArgs = new String[] {
                        "Music/%",
                        "MUSIC/%",
                        "Music/",
                        "MUSIC/",
                        "%Music/%",
                        "%MUSIC/%"
                };
            } else {
                selection =
                        MediaStore.Audio.Media.IS_MUSIC + "!=0 AND (" +
                        MediaStore.Audio.Media.DATA + " LIKE ? OR " +
                        MediaStore.Audio.Media.DATA + " LIKE ?" +
                        ")";
                selectionArgs = new String[] { "%/Music/%", "%/MUSIC/%" };
            }

            String sortOrder = MediaStore.Audio.Media.DATE_ADDED + " DESC";

            Cursor cursor = cr.query(collection, projection, selection, selectionArgs, sortOrder);

            JSONArray arr = new JSONArray();
            if (cursor != null) {
                int idxId = cursor.getColumnIndexOrThrow(MediaStore.Audio.Media._ID);
                int idxTitle = cursor.getColumnIndexOrThrow(MediaStore.Audio.Media.TITLE);
                int idxArtist = cursor.getColumnIndexOrThrow(MediaStore.Audio.Media.ARTIST);
                int idxAlbum = cursor.getColumnIndexOrThrow(MediaStore.Audio.Media.ALBUM);
                int idxDur = cursor.getColumnIndexOrThrow(MediaStore.Audio.Media.DURATION);
                int idxMime = cursor.getColumnIndexOrThrow(MediaStore.Audio.Media.MIME_TYPE);

                int idxRel = -1;
                int idxData = -1;
                if (hasRelativePath) {
                    idxRel = cursor.getColumnIndex(MediaStore.MediaColumns.RELATIVE_PATH);
                } else {
                    idxData = cursor.getColumnIndex(MediaStore.Audio.Media.DATA);
                }

                int count = 0;
                while (cursor.moveToNext()) {
                    long id = cursor.getLong(idxId);
                    String title = cursor.getString(idxTitle);
                    String artist = cursor.getString(idxArtist);
                    String album = cursor.getString(idxAlbum);
                    long duration = cursor.getLong(idxDur);
                    String mime = cursor.getString(idxMime);

                    String relPath = "";
                    String dataPath = "";
                    if (hasRelativePath && idxRel >= 0) {
                        String rp = cursor.getString(idxRel);
                        relPath = (rp != null) ? rp : "";
                    } else if (!hasRelativePath && idxData >= 0) {
                        String dp = cursor.getString(idxData);
                        dataPath = (dp != null) ? dp : "";
                    }

                    Uri contentUri = Uri.withAppendedPath(collection, String.valueOf(id));

                    JSONObject o = new JSONObject();
                    o.put("title", title != null ? title : "");
                    o.put("artist", artist != null ? artist : "");
                    o.put("album", album != null ? album : "");
                    o.put("durationMs", duration);
                    o.put("uri", contentUri.toString());
                    o.put("mime", mime != null ? mime : "");
                    // Helpful for debugging / confirming filter behavior
                    o.put("relativePath", relPath);
                    o.put("dataPath", dataPath);

                    arr.put(o);

                    count++;
                    if (limit > 0 && count >= limit) break;
                }
                cursor.close();
            }

            JSONObject root = new JSONObject();
            root.put("tracks", arr);
            return root.toString();

        } catch (Exception e) {
            Log.e(TAG, "queryMusicJson failed", e);
            try {
                JSONObject root = new JSONObject();
                root.put("tracks", new JSONArray());
                root.put("error", e.toString());
                return root.toString();
            } catch (Exception ignore) {
                return "{\"tracks\":[],\"error\":\"" + e.toString() + "\"}";
            }
        }
    }

    /**
     * Copy content:// uri to app cache and return absolute file path.
     * Returns "" on failure.
     */
    public static String copyUriToCache(Activity activity, String uriString) {
        if (activity == null || uriString == null || uriString.length() == 0) return "";
        try {
            Uri uri = Uri.parse(uriString);
            ContentResolver cr = activity.getContentResolver();

            String mime = cr.getType(uri);
            String ext = extensionFromMime(mime);
            if (ext == null || ext.length() == 0) ext = guessExtensionFromUri(uriString);
            if (ext == null || ext.length() == 0) ext = "audio";

            File outDir = activity.getCacheDir();
            if (!outDir.exists()) outDir.mkdirs();

            String outName = "kmusic_track_" + System.currentTimeMillis() + "." + ext;
            File outFile = new File(outDir, outName);

            InputStream in = cr.openInputStream(uri);
            if (in == null) return "";

            FileOutputStream out = new FileOutputStream(outFile);
            byte[] buf = new byte[64 * 1024];
            int n;
            while ((n = in.read(buf)) > 0) {
                out.write(buf, 0, n);
            }
            out.flush();
            out.close();
            in.close();

            return outFile.getAbsolutePath();
        } catch (Exception e) {
            Log.e(TAG, "copyUriToCache failed", e);
            return "";
        }
    }

    private static String extensionFromMime(String mime) {
        if (mime == null) return "";
        String ext = MimeTypeMap.getSingleton().getExtensionFromMimeType(mime);
        if (ext != null) return ext;
        if (mime.equals("audio/mpeg")) return "mp3";
        if (mime.equals("audio/mp4") || mime.equals("audio/aac")) return "m4a";
        if (mime.equals("audio/ogg")) return "ogg";
        if (mime.equals("audio/wav") || mime.equals("audio/x-wav")) return "wav";
        return "";
    }

    private static String guessExtensionFromUri(String uriString) {
        try {
            int q = uriString.indexOf('?');
            String s = q >= 0 ? uriString.substring(0, q) : uriString;
            int dot = s.lastIndexOf('.');
            if (dot >= 0 && dot < s.length() - 1) return s.substring(dot + 1);
        } catch (Exception ignore) {}
        return "";
    }

    /** Convenience for Unity: returns SDK_INT. */
    public static int sdkInt() {
        return Build.VERSION.SDK_INT;
    }
}