import com.connectipn.LinkClient;

import java.io.File;

public final class TestClientMain {
    public static void main(String[] args) throws Exception {
        String host = args[0];
        int port = Integer.parseInt(args[1]);
        String token = args[2];
        File root = new File(args[3]);

        LinkClient client = new LinkClient();
        Exception last = null;
        for (int attempt = 0; attempt < 100; attempt++) {
            try {
                client.connect(host, port, token, "JVM-Test", root);
                last = null;
                break;
            } catch (Exception e) {
                last = e;
                Thread.sleep(200);
            }
        }
        if (last != null) {
            System.out.println("JAVA-FAILED " + last);
            System.exit(1);
        }
        System.out.println("JAVA-CONNECTED " + client.rootPath());
        Thread.sleep(90000);
        client.disconnect();
    }
}
